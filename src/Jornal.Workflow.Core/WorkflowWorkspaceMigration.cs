namespace Jornal.Workflow.Core;

public static class WorkflowWorkspaceMigration
{
    public static WorkflowWorkspace Upgrade(WorkflowWorkspace workspace)
    {
        if (workspace.SchemaVersion == 7) return workspace;
        if (workspace.SchemaVersion is 5 or 6)
            return UpgradeTrashMetadata(workspace);
        if (workspace.SchemaVersion is 3 or 4)
        {
            // Materials and tariff categories now belong only to specification revisions.
            // Unknown schema-3 preparation fields are left in the original file backup by the store.
            return UpgradeOrderNumbers(workspace);
        }
        if (workspace.SchemaVersion == 2) return UpgradeProjects(workspace);
        if (workspace.SchemaVersion != 1 || workspace.Machines is null || workspace.Rates is null || workspace.Orders is null)
            throw new WorkflowValidationException("Неизвестная версия локальных данных. Файл не изменён.");
        var machines = workspace.Machines.ToList();
        foreach (var name in WorkflowMachine.Defaults().Select(m => m.Name).Concat(workspace.Rates.Select(r => r.Machine))
            .Concat(workspace.Orders.SelectMany(o => o.Specifications).SelectMany(s => s.Revisions).Select(r => r.Parameters.Machine)))
            if (!machines.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) machines.Add(WorkflowMachine.Defaults().SingleOrDefault(m => m.Name == name) ?? new(name, null));
        machines = machines.Select(m => m with
        {
            Id = m.Id == Guid.Empty ? WorkflowMachine.Defaults().SingleOrDefault(b => b.Name == m.Name)?.Id ?? Guid.NewGuid() : m.Id,
            LegacyName = m.LegacyName ?? m.Name,
            LegacyKey = m.LegacyKey ?? (m.Name is "Лазер" or "Плазма" or "Газо-кислородная" or "HansLaser" ? "legacy:" + m.Name : null)
        }).ToList();
        Guid Id(string name) => machines.Single(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Id;
        var next = workspace with
        {
            SchemaVersion = 2, Machines = machines.ToArray(),
            Rates = workspace.Rates.Select(r => r with { MachineId = Id(r.Machine) }).ToArray(),
            Orders = workspace.Orders.Select(o => o with
            {
                Specifications = o.Specifications.Select(s => s with
                {
                    Revisions = s.Revisions.Select(r => r with { Parameters = r.Parameters with { MachineId = Id(r.Parameters.Machine) } }).ToArray()
                }).ToArray()
            }).ToArray()
        };
        return UpgradeProjects(next);
    }

    private static WorkflowWorkspace UpgradeProjects(WorkflowWorkspace workspace)
    {
        var next = workspace with
        {
            SchemaVersion = 4,
            Orders = workspace.Orders.Select(order =>
            {
                var items = new List<WorkflowOrderItem>(); var projects = new List<CuttingProject>();
                foreach (var spec in order.Specifications)
                {
                    var itemId = Guid.NewGuid(); var r = spec.Current;
                    items.Add(new(itemId, items.Count + 1, spec.Id));
                    // No native file is fabricated for an old imported report.
                    projects.Add(new(spec.Id, itemId, 1, r.Parameters.MachineId,
                        r.Parameters.ThicknessMm, r.DocumentNumber, null,
                        r.ImportProfile, r.SavedAt));
                }
                return order with { Items = items.ToArray(), Projects = projects.ToArray() };
            }).ToArray()
        };
        return UpgradeOrderNumbers(next);
    }

    private static WorkflowWorkspace UpgradeOrderNumbers(WorkflowWorkspace workspace)
    {
        var used = workspace.Orders.Where(o => !string.IsNullOrEmpty(o.ReferenceNumber))
            .Select(o => o.ReferenceNumber).ToHashSet(StringComparer.Ordinal);
        var numbers = workspace.Orders.OrderBy(o => o.Number).ThenBy(o => o.Id).ToDictionary(o => o.Id,
            o => string.IsNullOrEmpty(o.ReferenceNumber) ? WorkflowOrderNumbers.Allocate(used, o.CreatedAt.ToLocalTime()) : o.ReferenceNumber);
        var next = workspace with
        {
            SchemaVersion = 7,
            Orders = workspace.Orders.Select(o => o with { ReferenceNumber = numbers[o.Id] }).ToArray()
        };
        return UpgradeTrashMetadata(next);
    }

    private static WorkflowWorkspace UpgradeTrashMetadata(WorkflowWorkspace workspace)
    {
        var next = workspace with
        {
            SchemaVersion = 7,
            Orders = workspace.Orders.Select(order => order with
            {
                CreatedById = order.CreatedById ?? (order.Events.FirstOrDefault() is { Description: "Создана заявка" } creation &&
                    workspace.Employees.Any(e => e.Id == creation.ActorId) ? creation.ActorId : null)
            }).ToArray()
        };
        next.Validate(); return next;
    }
}
