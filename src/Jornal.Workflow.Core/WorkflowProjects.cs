using System.Globalization;

namespace Jornal.Workflow.Core;

public sealed record WorkflowOrderItem(Guid Id, int Number, Guid CurrentProjectId)
{
    public int ReviewedThroughVariant { get; init; } = 1;
}
// Specification ID equals project ID. A prepared project has no specification until its first accepted calculation.
public sealed record CuttingProject(Guid Id, Guid ItemId, int Variant, Guid MachineId,
    decimal ThicknessMm, string Name, string? NativePath, string? ImportProfile,
    DateTimeOffset CreatedAt);

public static class WorkflowProjectNames
{
    public static string Extension(WorkflowMachine machine)
    {
        var extension = machine.ProjectExtension ?? (machine.ImportProfile switch
        {
            "hans-pdf/v1" => ".Dsp", "waterjet-awjc/v1" => ".dig",
            "block-html/v1" => ".rcam", _ => ".aucb"
        });
        if (extension.Length is < 2 or > 12 || extension[0] != '.' || !extension[1..].All(char.IsAsciiLetterOrDigit) ||
            extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            throw new WorkflowValidationException("Расширение проекта: точка и латинские буквы/цифры, например .rcam. HTML/PDF — отчёты.");
        return extension;
    }

    public static string Tag(WorkflowMachine machine) => machine.ImportProfile switch
    {
        "hans-pdf/v1" => "HANSLASER", "waterjet-awjc/v1" => "AWJC",
        "block-html/v1" => (machine.LegacyName ?? machine.Name) switch
        {
            "Плазма" => "ПЛАЗМА", "Газо-кислородная" => "ГАЗО-КИСЛОРОДНАЯ", _ => "ЛАЗЕР"
        },
        _ => "ТРУБОРЕЗ"
    };

    public static string Name(WorkflowMachine machine, decimal thickness, DateTimeOffset at) =>
        (machine.ImportProfile is null ? "" : thickness.ToString("0.############################", CultureInfo.InvariantCulture) +
        (machine.ImportProfile == "hans-pdf/v1" ? " mm " : " мм ")) +
        at.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) + "--" + Tag(machine);
}

public static class WorkflowProjects
{
    public static WorkflowWorkspace SetRoot(WorkflowWorkspace workspace, string? root)
    {
        root = string.IsNullOrWhiteSpace(root) ? null : root.Trim();
        if (root is not null && (!Path.IsPathFullyQualified(root) || root.Any(char.IsControl)))
            throw new WorkflowValidationException("Укажите полный путь к папке проектов.");
        return workspace.ProjectRoot == root ? workspace : workspace with { ProjectRoot = root };
    }

    public static WorkflowWorkspace Add(WorkflowWorkspace workspace, Guid orderId, long version,
        CuttingProject project, Guid actor, DateTimeOffset at)
    {
        var order = WorkflowChanges.RequireActiveOrder(workspace, orderId, version);
        if (!workspace.Employees.Any(e => e.Id == actor)) throw new WorkflowValidationException("Выберите технолога.");
        if (order.Projects.SingleOrDefault(p => p.Id == project.Id) is { } existing)
        {
            if (existing == project) return workspace;
            throw new WorkflowValidationException("Проект с таким ID уже существует.");
        }
        var item = order.Items.SingleOrDefault(i => i.Id == project.ItemId);
        if (project.Variant != order.Projects.Where(p => p.ItemId == project.ItemId).Select(p => p.Variant).DefaultIfEmpty().Max() + 1)
            throw new WorkflowConflictException();
        var items = item is null ? [.. order.Items, new WorkflowOrderItem(project.ItemId,
            order.Items.Select(i => i.Number).DefaultIfEmpty().Max() + 1, project.Id)] : order.Items;
        var next = order with { Items = items, Projects = [.. order.Projects, project],
            Version = order.Version + 1, UpdatedAt = at,
            Events = [.. order.Events, new(at, actor, item is null ? $"Подготовлен проект {project.Name}" : $"Подготовлен вариант {project.Variant}: {project.Name}")] };
        Validate(next, workspace.Machines);
        return workspace with { Orders = workspace.Orders.Select(o => o.Id == orderId ? next : o).ToArray() };
    }

    public static WorkflowWorkspace Select(WorkflowWorkspace workspace, Guid orderId, long version, Guid projectId,
        Guid actor, DateTimeOffset at)
    {
        var order = WorkflowChanges.RequireActiveOrder(workspace, orderId, version);
        if (!workspace.Employees.Any(e => e.Id == actor)) throw new WorkflowValidationException("Выберите технолога.");
        var project = order.Projects.Single(p => p.Id == projectId);
        var item = order.Items.Single(i => i.Id == project.ItemId);
        var latestVariant = order.Projects.Where(p => p.ItemId == item.Id).Max(p => p.Variant);
        if (item.CurrentProjectId == project.Id && item.ReviewedThroughVariant == latestVariant) return workspace;
        if (!order.Specifications.Any(s => s.Id == project.Id))
            throw new WorkflowValidationException("Сначала сохраните расчёт варианта, затем включите его в итог.");
        var next = order with { Items = order.Items.Select(i => i.Id == item.Id ? i with { CurrentProjectId = project.Id,
            ReviewedThroughVariant = latestVariant } : i).ToArray(),
            Version = order.Version + 1, UpdatedAt = at,
            Events = [.. order.Events, new(at, actor, $"Для позиции {item.Number} выбран вариант {project.Variant}: {project.Name}")] };
        return workspace with { Orders = workspace.Orders.Select(o => o.Id == orderId ? next : o).ToArray() };
    }

    public static void Validate(WorkflowOrder order, IReadOnlyList<WorkflowMachine> machines)
    {
        if (order.Items is null || order.Projects is null ||
            order.Items.Select(i => i.Id).Distinct().Count() != order.Items.Length ||
            order.Items.Any(i => i.Id == Guid.Empty || i.Number <= 0 || i.ReviewedThroughVariant <= 0) ||
            order.Items.Select(i => i.Number).Distinct().Count() != order.Items.Length ||
            order.Projects.Select(p => p.Id).Distinct().Count() != order.Projects.Length ||
            order.Projects.Select(p => (p.ItemId, p.Variant)).Distinct().Count() != order.Projects.Length ||
            order.Projects.Any(p => p.Id == Guid.Empty || p.Variant <= 0 || p.ThicknessMm <= 0 ||
                string.IsNullOrWhiteSpace(p.Name) || !machines.Any(m => m.Id == p.MachineId) ||
                !order.Items.Any(i => i.Id == p.ItemId)) ||
            order.Items.Any(i => !order.Projects.Any(p => p.ItemId == i.Id && p.Id == i.CurrentProjectId)))
            throw new WorkflowValidationException("Нарушены связи позиций и вариантов проектов.");
        foreach (var machine in machines) WorkflowProjectNames.Extension(machine);
    }
}
