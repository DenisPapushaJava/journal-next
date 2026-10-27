namespace Jornal.Workflow.Core;

public sealed record WorkflowShopHandoff(long OrderVersion, Guid ActorId, DateTimeOffset At);

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
        var stamp = new WorkflowShopHandoff(expectedVersion, actor, DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds()));
        if (order.ShopHandoff == stamp) return workspace;
        // A handoff is metadata for this saved version, not a new calculation or an edit of the order.
        return workspace with { Orders = workspace.Orders.Select(o => o.Id == orderId ? o with { ShopHandoff = stamp } : o).ToArray() };
    }
}
