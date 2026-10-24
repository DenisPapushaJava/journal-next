using Jornal.Core.Pricing;

namespace Jornal.Core.Orders;

/// <summary>Все черновики сохраняются атомарно; последовательные независимые CreateAsync не заменяют этот контракт.</summary>
public interface IManualOrderBatchCreator
{
    Task<IReadOnlyList<OrderSummary>> CreateBatchAsync(IReadOnlyList<ManualOrderDraft> drafts, CancellationToken cancellationToken = default);
}

public static class ManualOrderBatch
{
    public const int MaximumCount = 100;
    public static ManualOrderDraft[] Snapshot(IReadOnlyList<ManualOrderDraft> drafts)
    {
        if (drafts.Count is < 1 or > MaximumCount)
            throw new OrderWriteException(OrderWriteFailure.NotSaved, $"В пакете должно быть от 1 до {MaximumCount} заявок.");
        var snapshot = drafts.ToArray();
        foreach (var draft in snapshot)
        {
            if (draft is null) throw new OrderWriteException(OrderWriteFailure.NotSaved, "В пакете отсутствуют параметры заявки.");
            draft.Validate();
        }
        return snapshot;
    }
}

public sealed class OrderBatchPriceChangedException(int index, CuttingPriceQuote quote) : Exception(
    "Прайс изменился. Пакет не добавлен. Проверьте новую цену и повторите сохранение.")
{
    public int Index { get; } = index;
    public CuttingPriceQuote Quote { get; } = quote;
}
