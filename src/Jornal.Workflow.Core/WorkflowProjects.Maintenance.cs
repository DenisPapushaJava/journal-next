namespace Jornal.Workflow.Core;

public static partial class WorkflowProjects
{
    public static ProjectRename ProposeRename(CuttingProject project, WorkflowMachine machine, decimal thickness,
        Guid actor, DateTimeOffset at)
    {
        if (project.IsDeleted) throw new WorkflowValidationException("Проект находится в корзине.");
        if (thickness <= 0) throw new WorkflowValidationException("Укажите положительную толщину.");
        if (project.NativePath is not { } native || !Path.IsPathFullyQualified(native))
            throw new WorkflowValidationException("У этого проекта нет созданной программой заготовки.");
        var parent = Path.GetDirectoryName(Path.GetDirectoryName(native)) ?? throw new WorkflowValidationException("Не задана папка проекта.");
        var name = WorkflowProjectNames.Renamed(project, machine, thickness);
        return new(Guid.NewGuid(), machine.Id, thickness, name, Path.Combine(parent, name, name + WorkflowProjectNames.Extension(machine)),
            machine.ImportProfile, actor, at);
    }

    public static WorkflowWorkspace BeginRename(WorkflowWorkspace book, Guid orderId, long version, Guid projectId,
        Guid machineId, decimal thickness, Guid actor, DateTimeOffset at)
        => BeginRename(book, orderId, version, projectId, machineId, thickness, actor, at, null);

    public static WorkflowWorkspace BeginRename(WorkflowWorkspace book, Guid orderId, long version, Guid projectId,
        ProjectRename confirmedPlan)
        => BeginRename(book, orderId, version, projectId, confirmedPlan.MachineId, confirmedPlan.ThicknessMm,
            confirmedPlan.ActorId, confirmedPlan.StartedAt, confirmedPlan);

    private static WorkflowWorkspace BeginRename(WorkflowWorkspace book, Guid orderId, long version, Guid projectId,
        Guid machineId, decimal thickness, Guid actor, DateTimeOffset at, ProjectRename? confirmedPlan)
    {
        var order = WorkflowChanges.RequireActiveOrder(book, orderId, version, allowProjectRename: true);
        Actor(book, actor);
        var project = order.Projects.Single(p => p.Id == projectId);
        if (project.IsDeleted || order.Specifications.Any(s => s.Id == projectId))
            throw new WorkflowValidationException("Можно изменять только пустую заготовку без принятой спецификации. Для готового проекта используйте другой вариант.");
        if (project.Rename is { } pending)
        {
            if (confirmedPlan is not null && !pending.SameOperation(confirmedPlan)) throw new WorkflowConflictException();
            if (pending.MachineId != machineId || pending.ThicknessMm != thickness)
                throw new WorkflowValidationException("Сначала завершите начатое переименование проекта.");
            return book;
        }
        if (order.HasProjectRename) throw new WorkflowValidationException("Сначала завершите изменение другого проекта заявки.");
        var machine = book.Machines.SingleOrDefault(m => m.Id == machineId) ?? throw new WorkflowConflictException();
        if (machine.Id == project.MachineId && thickness == project.ThicknessMm) return book;
        var plan = ProposeRename(project, machine, thickness, actor, at);
        if (confirmedPlan is not null)
        {
            if (plan with { Id = confirmedPlan.Id } != confirmedPlan) throw new WorkflowConflictException();
            plan = confirmedPlan;
        }
        return Replace(book, order, project with { Rename = plan }, at);
    }

    public static WorkflowWorkspace CompleteRename(WorkflowWorkspace book, Guid orderId, long version, Guid projectId, Guid operationId)
    {
        var order = WorkflowChanges.RequireActiveOrder(book, orderId, version, allowProjectRename: true);
        var project = order.Projects.Single(p => p.Id == projectId);
        var plan = project.Rename;
        if (plan is null || plan.Id != operationId) throw new WorkflowConflictException();
        return Replace(book, order, project with
        {
            MachineId = plan.MachineId, ThicknessMm = plan.ThicknessMm, Name = plan.Name,
            NativePath = plan.NativePath, ImportProfile = plan.ImportProfile, Rename = null
        }, DateTimeOffset.UtcNow);
    }

    public static WorkflowWorkspace CancelUnstartedRename(WorkflowWorkspace book, Guid orderId, long version, Guid projectId, Guid operationId)
    {
        var order = WorkflowChanges.RequireActiveOrder(book, orderId, version, allowProjectRename: true);
        var project = order.Projects.Single(p => p.Id == projectId);
        if (project.Rename?.Id != operationId) throw new WorkflowConflictException();
        return Replace(book, order, project with { Rename = null }, DateTimeOffset.UtcNow);
    }

    public static WorkflowWorkspace SetTrash(WorkflowWorkspace book, Guid orderId, long version, Guid projectId,
        bool deleted, Guid actor, DateTimeOffset at)
    {
        var order = WorkflowChanges.RequireActiveOrder(book, orderId, version);
        Actor(book, actor);
        var project = order.Projects.Single(p => p.Id == projectId);
        if (project.IsDeleted == deleted) return book;
        // Keep the selected ID: deleting it must never silently select a different calculation.
        return Replace(book, order, project with { DeletedAt = deleted ? at : null, DeletedBy = deleted ? actor : null }, at);
    }

    public static WorkflowWorkspace SetIncluded(WorkflowWorkspace book, Guid orderId, long version, Guid itemId,
        bool included, Guid actor, DateTimeOffset at)
    {
        var order = WorkflowChanges.RequireActiveOrder(book, orderId, version); Actor(book, actor);
        var item = order.Items.Single(i => i.Id == itemId);
        if (item.IsIncluded == included) return book;
        var next = order with
        {
            Items = order.Items.Select(i => i.Id == itemId ? i with { IsIncluded = included } : i).ToArray(),
            Version = order.Version + 1, UpdatedAt = at
        };
        return book with { Orders = book.Orders.Select(o => o.Id == order.Id ? next : o).ToArray() };
    }

    private static void Actor(WorkflowWorkspace book, Guid actor)
    { if (!book.Employees.Any(e => e.Id == actor && e.CanWork)) throw new WorkflowValidationException("Выберите действующего технолога в настройках."); }

    private static WorkflowWorkspace Replace(WorkflowWorkspace book, WorkflowOrder order, CuttingProject project,
        DateTimeOffset at)
    {
        var next = order with
        {
            Projects = order.Projects.Select(p => p.Id == project.Id ? project : p).ToArray(),
            Version = order.Version + 1, UpdatedAt = at
        };
        return book with { Orders = book.Orders.Select(o => o.Id == order.Id ? next : o).ToArray() };
    }
}
