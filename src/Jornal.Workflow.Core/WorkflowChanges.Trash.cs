namespace Jornal.Workflow.Core;

public static partial class WorkflowChanges
{
    internal static WorkflowOrder RequireActiveOrder(WorkflowWorkspace workspace, Guid id, long version)
    {
        var order = workspace.Orders.SingleOrDefault(o => o.Id == id) ?? throw new WorkflowValidationException("Заявка не найдена.");
        if (order.IsDeleted) throw new WorkflowValidationException("Заявка находится в корзине. Восстановите её перед изменением.");
        if (order.Version != version) throw new WorkflowConflictException();
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
        if (!workspace.Employees.Any(e => e.Id == actor)) throw new WorkflowValidationException("Выберите себя: Настройки → Мой профиль.");
        if (order.IsDeleted == deleted) return workspace;
        var next = order with
        {
            DeletedAt = deleted ? at : null, DeletedBy = deleted ? actor : null,
            Version = order.Version + 1, UpdatedAt = at,
            Events = [.. order.Events, new(at, actor, deleted ? "Перемещена в корзину" : "Восстановлена из корзины")]
        };
        return workspace with { Orders = workspace.Orders.Select(o => o.Id == id ? next : o).ToArray() };
    }
}
