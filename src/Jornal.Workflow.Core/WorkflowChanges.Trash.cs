namespace Jornal.Workflow.Core;

public sealed record WorkflowTrashTarget(Guid OrderId, long Version);

public static partial class WorkflowChanges
{
    public static WorkflowWorkspace PurgeTrashedOrders(WorkflowWorkspace workspace, IReadOnlyList<WorkflowTrashTarget> targets, Guid actor)
    {
        if (!workspace.Employees.Any(e => e.Id == actor && e.CanWork))
            throw new WorkflowValidationException("Выберите себя: Настройки → Общие → Технолог.");
        if (targets.Count == 0) throw new WorkflowValidationException("В корзине нет выбранных заявок.");
        var ids = targets.Select(t => t.OrderId).ToHashSet();
        if (ids.Count != targets.Count) throw new WorkflowValidationException("Список удаления содержит повторные заявки.");
        var orders = workspace.Orders.ToDictionary(o => o.Id);
        // Validate the entire frozen scope before removing anything. Newly trashed orders are outside it.
        foreach (var target in targets)
        {
            if (!orders.TryGetValue(target.OrderId, out var order) || order.Version != target.Version || !order.IsDeleted)
                throw new WorkflowConflictException();
            if (order.HasProjectRename)
                throw new WorkflowValidationException("У заявки не завершено изменение проекта. Сначала завершите его.");
        }
        return workspace with { Orders = workspace.Orders.Where(o => !ids.Contains(o.Id)).ToArray() };
    }

    internal static WorkflowOrder RequireActiveOrder(WorkflowWorkspace workspace, Guid id, long version, bool allowProjectRename = false)
    {
        var order = workspace.Orders.SingleOrDefault(o => o.Id == id) ?? throw new WorkflowValidationException("Заявка не найдена.");
        if (order.IsDeleted) throw new WorkflowValidationException("Заявка находится в корзине. Восстановите её перед изменением.");
        if (order.Version != version) throw new WorkflowConflictException();
        if (!allowProjectRename && order.HasProjectRename)
            throw new WorkflowValidationException("Сначала завершите изменение параметров проекта во вкладке «Проекты».");
        return order;
    }

    public static WorkflowWorkspace MoveOrderToTrash(WorkflowWorkspace workspace, Guid id, long version, Guid actor, DateTimeOffset at) =>
        SetTrashState(workspace, id, version, actor, at, true);
    public static WorkflowWorkspace RestoreOrder(WorkflowWorkspace workspace, Guid id, long version, Guid actor, DateTimeOffset at) =>
        SetTrashState(workspace, id, version, actor, at, false);

    private static WorkflowWorkspace SetTrashState(WorkflowWorkspace workspace, Guid id, long version, Guid actor, DateTimeOffset at, bool deleted)
    {
        var order = workspace.Orders.SingleOrDefault(o => o.Id == id) ?? throw new WorkflowValidationException("Заявка не найдена.");
        if (order.Version != version) throw new WorkflowConflictException();
        if (order.HasProjectRename) throw new WorkflowValidationException("Сначала завершите изменение параметров проекта.");
        if (!workspace.Employees.Any(e => e.Id == actor && e.CanWork)) throw new WorkflowValidationException("Выберите себя: Настройки → Общие → Технолог.");
        if (order.IsDeleted == deleted) return workspace;
        var next = order with
        {
            DeletedAt = deleted ? at : null, DeletedBy = deleted ? actor : null,
            Version = order.Version + 1, UpdatedAt = at
        };
        return workspace with { Orders = workspace.Orders.Select(o => o.Id == id ? next : o).ToArray() };
    }
}
