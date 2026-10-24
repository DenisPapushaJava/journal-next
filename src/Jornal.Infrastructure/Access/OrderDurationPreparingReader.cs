using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Access;

// Только рабочее подключение UI инициирует миграцию; обычный IOrderReader остаётся read-only.
public sealed class OrderDurationPreparingReader(IOrderReader reader, IOrderDurationInitializer initializer, IOrderAnnotationsInitializer? annotations = null) : IOrderReader
{
    private bool _prepared;
    private bool _annotationsPrepared;
    public string? PreparationWarning { get; private set; }
    // Фоновая проверка не инициирует/не повторяет подготовку схемы.
    public Task<IReadOnlyList<OrderSummary>> ReadSnapshotAsync(DateOnly? date, CancellationToken cancellationToken = default) =>
        date is { } day ? reader.ReadDayAsync(day, cancellationToken) : reader.ReadAsync(cancellationToken);
    public async Task<IReadOnlyList<OrderSummary>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var rows = await reader.ReadAsync(cancellationToken);
        return await PrepareAsync(rows, cancellationToken);
    }

    public async Task<IReadOnlyList<OrderSummary>> ReadDayAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var rows = await reader.ReadDayAsync(date, cancellationToken);
        return await PrepareAsync(rows, cancellationToken);
    }

    private async Task<IReadOnlyList<OrderSummary>> PrepareAsync(IReadOnlyList<OrderSummary> rows, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        if (!_prepared)
        {
            try { await initializer.PrepareCuttingTimeAsync(cancellationToken); _prepared = true; }
            catch (OrderWriteException exception) { warnings.Add(exception.Message); }
        }
        if (annotations is not null && !_annotationsPrepared)
        {
            try { await annotations.PrepareAnnotationsAsync(cancellationToken); _annotationsPrepared = true; }
            catch (OrderWriteException exception) { warnings.Add(exception.Message); }
        }
        if (rows.Any(row => row.AnnotationsUnavailable)) warnings.Add(OrderAnnotations.UnavailableMessage);
        PreparationWarning = warnings.Count > 0 ? string.Join("\n", warnings) : null;
        return rows;
    }
}
