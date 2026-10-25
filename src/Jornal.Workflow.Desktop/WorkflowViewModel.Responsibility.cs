using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowResponsibleChange(Guid OrderId, long Version, Guid EmployeeId, string OrderTitle, Employee[] Employees);

public partial class WorkflowViewModel
{
    public WorkflowResponsibleChange? CreateResponsibleChange()
    {
        if (!CanUse || IsCreating || !IsCurrentOrderActive || CurrentOrder is not { } order) return null;
        if (IsCurrentCardStale)
        { Error = "Обновите карточку и проверьте заявку перед сменой ответственного."; return null; }
        return new(order.Id, CardVersion, order.TechnologistId, OrderTitle,
            Employees.Where(e => e.CanWork || e.Id == order.TechnologistId).ToArray());
    }

    public async Task ChangeResponsibleAsync(WorkflowResponsibleChange request, Guid employeeId)
    {
        if (!CanUse) return;
        if (IsCreating || !IsCurrentOrderActive || CurrentOrder is not { } order || order.Id != request.OrderId ||
            order.Version != request.Version || CardVersion != request.Version)
        { Error = "Карточка изменилась. Проверьте заявку и откройте смену ответственного заново."; return; }
        if (order.TechnologistId == employeeId) return;
        await RunAsync(async () =>
        {
            var actorId = ActorId();
            // Save only the responsible employee, leaving locally edited header fields and calculations untouched.
            _workspace = await _store.WriteOrderAsync(order.Id, request.Version, book => WorkflowChanges.UpdateOrder(book,
                order.Id, request.Version, order.ManagerId, employeeId, order.Blcb, actorId, DateTimeOffset.UtcNow));
            var saved = _workspace.Orders.Single(o => o.Id == order.Id);
            _loadedOrderVersion = saved.Version; _loadedOrderSnapshot = saved;
            Technologist = _workspace.Employees.Single(e => e.Id == saved.TechnologistId);
            RefreshOrders(preserveRows: true); NotifyOrder();
        }, "Не удалось подтвердить смену ответственного. Обновите журнал перед повтором; автоматического повтора нет.");
    }
}
