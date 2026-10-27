using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jornal.Workflow.Core;

public static class WorkflowWorkspaceMigration
{
    public static WorkflowWorkspace Upgrade(WorkflowWorkspace workspace) => workspace.SchemaVersion == WorkflowWorkspace.CurrentSchema ? workspace :
        Upgrade(JsonSerializer.SerializeToNode(workspace)!.AsObject());

    // Legacy history exists only at this read boundary. The store backs up the original bytes before saving.
    public static WorkflowWorkspace Upgrade(JsonObject source)
    {
        var schema = source["SchemaVersion"]?.GetValue<int>() ?? 0;
        if (schema is < 1 or > WorkflowWorkspace.CurrentSchema) throw new WorkflowValidationException("Неизвестная версия локальных данных. Файл не изменён.");
        var data = source.DeepClone().AsObject();
        var technicalActors = new HashSet<Guid>();
        var employeeIds = (data["Employees"] as JsonArray ?? throw Invalid()).Select(e => e!["Id"]!.GetValue<Guid>()).ToHashSet();
        foreach (var order in data["Orders"] as JsonArray ?? throw Invalid())
        {
            Remember(order, "TechnologistId"); Remember(order, "CreatedById"); Remember(order, "DeletedBy");
            if (order!["Events"] is JsonArray events)
            {
                foreach (var e in events) Remember(e, "ActorId");
                if (schema <= 6 && order["CreatedById"] is null && events.FirstOrDefault() is { } creation &&
                    creation["Description"]?.GetValue<string>() == "Создана заявка" && creation["ActorId"]?.GetValue<Guid>() is { } actor && employeeIds.Contains(actor))
                    order["CreatedById"] = actor;
            }
            order.AsObject().Remove("Events");
            foreach (var project in order["Projects"] as JsonArray ?? [])
            { Remember(project, "DeletedBy"); Remember(project?["Rename"], "ActorId"); }
            foreach (var spec in order["Specifications"] as JsonArray ?? throw Invalid())
            {
                if (spec!["Revisions"] is JsonArray revisions)
                {
                    if (revisions.Count == 0) throw Invalid();
                    for (var i = 0; i < revisions.Count; i++)
                    {
                        if (revisions[i] is not JsonObject) throw Invalid();
                        Remember(revisions[i], "AuthorId");
                        if (revisions[i]?["Number"]?.GetValue<int>() is { } number && number != i + 1) throw Invalid();
                    }
                    spec["Current"] = revisions[^1]!.DeepClone();
                }
                spec.AsObject().Remove("Revisions");
                if (spec["Current"] is not JsonObject current) throw Invalid();
                Remember(current, "AuthorId"); current.Remove("Id"); current.Remove("Number");
            }
        }
        data["SchemaVersion"] = WorkflowWorkspace.CurrentSchema;
        var next = data.Deserialize<WorkflowWorkspace>() ?? throw Invalid();
        if (schema == 1) next = UpgradeMachines(next);
        if (schema <= 2) next = UpgradeProjects(next);
        if (schema <= 4)
        {
            var used = next.Orders.Where(o => !string.IsNullOrEmpty(o.ReferenceNumber)).Select(o => o.ReferenceNumber).ToHashSet(StringComparer.Ordinal);
            var numbers = next.Orders.OrderBy(o => o.Number).ThenBy(o => o.Id).ToDictionary(o => o.Id,
                o => string.IsNullOrEmpty(o.ReferenceNumber) ? WorkflowOrderNumbers.Allocate(used, o.CreatedAt.ToLocalTime()) : o.ReferenceNumber);
            next = next with { Orders = next.Orders.Select(o => o with { ReferenceNumber = numbers[o.Id] }).ToArray() };
        }
        if (schema <= 9)
        {
            var managers = next.Orders.Select(o => o.ManagerId).ToHashSet();
            next = next with { Employees = next.Employees.Select(e => e with
            { IsTechnologist = e.IsTechnologist || e.ColorArgb is not null || technicalActors.Contains(e.Id) || !managers.Contains(e.Id) }).ToArray() };
        }
        next.Validate(); return next;

        void Remember(JsonNode? node, string field)
        { if (node?[field]?.GetValue<Guid>() is { } id) technicalActors.Add(id); }
    }

    private static WorkflowValidationException Invalid() => new("Повреждены сохранённые данные спецификаций. Файл не изменён.");

    private static WorkflowWorkspace UpgradeMachines(WorkflowWorkspace workspace)
    {
        var machines = workspace.Machines.ToList();
        foreach (var name in WorkflowMachine.Defaults().Select(m => m.Name).Concat(workspace.Rates.Select(r => r.Machine))
            .Concat(workspace.Orders.SelectMany(o => o.Specifications).Select(s => s.Current.Parameters.Machine)))
            if (!machines.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                machines.Add(WorkflowMachine.Defaults().SingleOrDefault(m => m.Name == name) ?? new(name, null));
        machines = machines.Select(m => m with
        {
            Id = m.Id == Guid.Empty ? WorkflowMachine.Defaults().SingleOrDefault(b => b.Name == m.Name)?.Id ?? Guid.NewGuid() : m.Id,
            LegacyName = m.LegacyName ?? m.Name,
            LegacyKey = m.LegacyKey ?? (m.Name is "Лазер" or "Плазма" or "Газо-кислородная" or "HansLaser" ? "legacy:" + m.Name : null)
        }).ToList();
        Guid Id(string name) => machines.Single(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Id;
        return workspace with
        {
            Machines = machines.ToArray(), Rates = workspace.Rates.Select(r => r with { MachineId = Id(r.Machine) }).ToArray(),
            Orders = workspace.Orders.Select(o => o with { Specifications = o.Specifications.Select(s => s with
            { Current = s.Current with { Parameters = s.Current.Parameters with { MachineId = Id(s.Current.Parameters.Machine) } } }).ToArray() }).ToArray()
        };
    }

    private static WorkflowWorkspace UpgradeProjects(WorkflowWorkspace workspace) => workspace with
    {
        Orders = workspace.Orders.Select(order =>
        {
            var items = new List<WorkflowOrderItem>(); var projects = new List<CuttingProject>();
            foreach (var spec in order.Specifications)
            {
                var itemId = Guid.NewGuid(); var saved = spec.Current;
                items.Add(new(itemId, items.Count + 1, spec.Id));
                projects.Add(new(spec.Id, itemId, 1, saved.Parameters.MachineId, saved.Parameters.ThicknessMm,
                    saved.DocumentNumber, null, saved.ImportProfile, saved.SavedAt));
            }
            return order with { Items = items.ToArray(), Projects = projects.ToArray() };
        }).ToArray()
    };
}
