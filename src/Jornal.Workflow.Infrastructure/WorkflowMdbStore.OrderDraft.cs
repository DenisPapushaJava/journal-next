using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed partial class WorkflowMdbStore
{
    public Task<WorkflowOrder> ImportEmptyOrderAsync(Guid id, string manager, string customer, string technologist,
        DateTimeOffset at, CancellationToken token = default) => SaveDraftAsync(id, manager, customer, at,
        current => current.Employees.Where(employee => employee.CanWork &&
            string.Equals(employee.Name, technologist, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray(), token);

    public Task<WorkflowOrder> CreateRemoteOrderAsync(Guid id, string manager, string customer, Guid technologist,
        DateTimeOffset at, CancellationToken token = default) => SaveDraftAsync(id, manager, customer, at,
        current => current.Employees.Where(employee => employee.CanWork && employee.Id == technologist).ToArray(), token);

    private async Task<WorkflowOrder> SaveDraftAsync(Guid id, string manager, string customer, DateTimeOffset at,
        Func<WorkflowWorkspace, Employee[]> findAuthors, CancellationToken token)
    {
        if (id == Guid.Empty || at == default) throw new WorkflowValidationException("Не указан ID или дата заявки.");
        var workspace = await Write([id], null, current =>
        {
            if (findAuthors(current) is not [var actor])
                throw new WorkflowValidationException("В MDB нет одного действующего технолога для этой заявки.");
            if (current.Orders.SingleOrDefault(order => order.Id == id) is { } existing)
            {
                if (existing.IsDeleted || existing.CreatedById != actor.Id) throw new WorkflowConflictException();
                return current;
            }

            var updated = WorkflowChanges.CreateOrderFromNames(current, manager, customer, actor.Id, at, out var generated);
            return updated with { Orders = updated.Orders.Select(order => order.Id == generated ? order with { Id = id } : order).ToArray() };
        }, token, creation: id).ConfigureAwait(false);

        return workspace.Orders.SingleOrDefault(order => order.Id == id) ?? throw new WorkflowOutcomeUnknownException();
    }
}
