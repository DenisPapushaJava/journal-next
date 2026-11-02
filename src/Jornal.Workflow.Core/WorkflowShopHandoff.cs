using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Jornal.Workflow.Core;

public sealed record WorkflowShopHandoff(long OrderVersion, Guid ActorId, DateTimeOffset At)
{
    public string? ContentHash { get; init; }
    public WorkflowProjectHandoffInputs? ProjectInputs { get; init; }

    // Saved workshop inputs only. Prices, coefficient, internal comments, authors and save times do not change the job.
    public static string Hash(WorkflowOrder order) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        order.ManagerId, order.CustomerId, Blcb = order.Blcb?.Trim(),
        Items = order.Items.Where(i => i.IsIncluded).OrderBy(i => i.Number).Select(i => new { i.Id, i.CurrentProjectId }).ToArray(),
        Projects = order.ActiveProjects.Where(p => order.Items.Any(i => i.IsIncluded && i.CurrentProjectId == p.Id)).OrderBy(p => p.Id)
            .Select(p => new { p.Id, p.MachineId, Thickness = Number(p.ThicknessMm), p.Name, p.ImportProfile }).ToArray(),
        Specifications = order.ActiveSpecifications.OrderBy(s => s.Id).Select(s => new
        {
            s.Id, s.Current.Parameters.MachineId, s.Current.Parameters.Material, Thickness = Number(s.Current.Parameters.ThicknessMm),
            s.Current.Parameters.BlankSize, Quantity = Number(s.Current.Parameters.BlankQuantity), s.Current.Parameters.HasBending, s.Current.Parameters.HasChamfer,
            Report = s.Current.Report?.Sha256.ToUpperInvariant()
        }).ToArray()
    })));
    public static string ProjectHash(WorkflowOrder order, Guid projectId)
    {
        var project = order.Projects.Single(p => p.Id == projectId);
        var item = order.Items.Single(i => i.Id == project.ItemId);
        return Hash(order with
        {
            Items = [item with { IsIncluded = true, CurrentProjectId = projectId }],
            Projects = [project with { DeletedAt = null, DeletedBy = null }],
            Specifications = order.Specifications.Where(s => s.Id == projectId).ToArray()
        });
    }
    // Handoffs change independently of the calculation version. A work-data replacement
    // must keep the latest recorded facts, including a concurrent explicit clear.
    public static WorkflowOrder PreserveRecorded(WorkflowOrder? current, WorkflowOrder replacement)
    {
        var marks = current?.Projects.Where(p => p.ShopHandoff is not null).ToDictionary(p => p.Id, p => p.ShopHandoff);
        return replacement with
        {
            ShopHandoff = current?.ShopHandoff,
            Projects = replacement.Projects.Select(p => p with { ShopHandoff = marks?.GetValueOrDefault(p.Id) }).ToArray()
        };
    }
    public void Validate(long version, IReadOnlyList<Employee> employees, bool requireHash = false)
    {
        ProjectInputs?.Validate();
        if (OrderVersion < 1 || OrderVersion > version || At == default || !employees.Any(e => e.Id == ActorId) ||
            requireHash && ContentHash is null || ContentHash is { } hash && (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))))
            throw new WorkflowValidationException("Повреждена отметка передачи в цех.");
    }
    private static string Number(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
}

public sealed record WorkflowProjectHandoffExpected(Guid ProjectId, WorkflowShopHandoff Stamp);
public sealed record WorkflowHandoffClearExpected(WorkflowShopHandoff? Order, WorkflowProjectHandoffExpected[] Projects)
{
    public static WorkflowHandoffClearExpected From(WorkflowOrder order) => new(order.ShopHandoff,
        order.Projects.Where(p => p.ShopHandoff is not null).OrderBy(p => p.Id).Select(p => new WorkflowProjectHandoffExpected(p.Id, p.ShopHandoff!)).ToArray());
    public bool Matches(WorkflowOrder order) => Projects is not null && Order == order.ShopHandoff &&
        Projects.SequenceEqual(From(order).Projects);
}

public static partial class WorkflowChanges
{
    public static WorkflowWorkspace RecordShopHandoff(WorkflowWorkspace workspace, Guid orderId, long expectedVersion, Guid actor, DateTimeOffset at)
    {
        var order = workspace.Orders.SingleOrDefault(o => o.Id == orderId);
        if (order?.Version != expectedVersion || order.IsDeleted) throw new WorkflowConflictException();
        if (!workspace.Employees.Any(e => e.Id == actor && e.CanWork))
            throw new WorkflowValidationException("Выберите действующего технолога перед передачей в цех.");
        if (at == default) throw new WorkflowValidationException("Проверьте время передачи в цех.");
        if (!WorkflowCuttingTask.Prepare(order, workspace.Employees, workspace.Customers, workspace.Machines).IsReady)
            throw new WorkflowValidationException("Заявка не готова к передаче в цех. Проверьте состав и сохранённые расчёты.");
        var stamp = new WorkflowShopHandoff(expectedVersion, actor, DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds())) { ContentHash = WorkflowShopHandoff.Hash(order) };
        var ids = order.ActiveProjects.Where(p => order.Items.Any(i => i.IsIncluded && i.CurrentProjectId == p.Id)).Select(p => p.Id).ToArray();
        var next = StampProjects(order, ids, actor, stamp.At);
        if (order.ShopHandoff == stamp && ReferenceEquals(next, order)) return workspace;
        return workspace with { Orders = workspace.Orders.Select(o => o.Id == orderId ? next with { ShopHandoff = stamp } : o).ToArray() };
    }
    public static WorkflowWorkspace RecordProjectHandoffs(WorkflowWorkspace workspace, Guid orderId, long expectedVersion, Guid[] projectIds, Guid actor, DateTimeOffset at)
    {
        var order = workspace.Orders.SingleOrDefault(o => o.Id == orderId);
        if (order?.Version != expectedVersion || order.IsDeleted) throw new WorkflowConflictException();
        if (projectIds is null || projectIds.Length == 0 || projectIds.Length > 512 || projectIds.Distinct().Count() != projectIds.Length ||
            projectIds.Any(id => !order.ActiveProjects.Any(p => p.Id == id && order.Items.Any(i => i.IsIncluded && i.CurrentProjectId == id)) ||
                !order.Specifications.Any(s => s.Id == id))) throw new WorkflowValidationException("Проверьте состав переданных проектов.");
        if (at == default || !workspace.Employees.Any(e => e.Id == actor && e.CanWork)) throw new WorkflowValidationException("Проверьте автора и дату передачи.");
        var next = StampProjects(order, projectIds, actor, DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds()));
        return ReferenceEquals(next, order) ? workspace : workspace with { Orders = workspace.Orders.Select(o => o.Id == orderId ? next : o).ToArray() };
    }
    private static WorkflowOrder StampProjects(WorkflowOrder order, Guid[] ids, Guid actor, DateTimeOffset at)
    {
        var selected = ids.ToHashSet(); var changed = false;
        var projects = order.Projects.Select(p =>
        {
            if (!selected.Contains(p.Id)) return p;
            var stamp = new WorkflowShopHandoff(order.Version, actor, at)
            { ContentHash = WorkflowShopHandoff.ProjectHash(order, p.Id), ProjectInputs = WorkflowProjectHandoffInputs.Capture(order, p.Id) };
            if (p.ShopHandoff == stamp) return p;
            changed = true; return p with { ShopHandoff = stamp };
        }).ToArray();
        return changed ? order with { Projects = projects } : order;
    }
    public static WorkflowWorkspace ClearShopHandoff(WorkflowWorkspace workspace, Guid orderId, long expectedVersion, WorkflowHandoffClearExpected expected, Guid actor)
    {
        if (expected is null) throw new WorkflowValidationException("Не передана снимаемая отметка.");
        var order = workspace.Orders.SingleOrDefault(o => o.Id == orderId);
        if (order?.Version != expectedVersion || order.IsDeleted || !expected.Matches(order)) throw new WorkflowConflictException();
        if (expected.Order is null && expected.Projects.Length == 0) throw new WorkflowValidationException("У заявки нет отметки передачи.");
        if (!workspace.Employees.Any(e => e.Id == actor && e.CanWork)) throw new WorkflowValidationException("Выберите действующего технолога.");
        return workspace with { Orders = workspace.Orders.Select(o => o.Id == orderId ? o with { ShopHandoff = null, Projects = o.Projects.Select(p => p with { ShopHandoff = null }).ToArray() } : o).ToArray() };
    }

}
