namespace Jornal.Core.Orders;

public interface IOrderReader
{
    Task<IReadOnlyList<OrderSummary>> ReadAsync(CancellationToken cancellationToken = default);

    // Файловые/демонстрационные адаптеры могут фильтровать снимок; Jet переопределяет запрос.
    async Task<IReadOnlyList<OrderSummary>> ReadDayAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var rows = await ReadAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return rows.Where(row => row.OrderDate == date).ToArray();
    }
}
