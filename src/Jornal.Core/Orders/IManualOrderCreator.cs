namespace Jornal.Core.Orders;

public interface IManualOrderCreator
{
    Task<OrderSummary> CreateAsync(ManualOrderDraft draft, CancellationToken cancellationToken = default);
}
