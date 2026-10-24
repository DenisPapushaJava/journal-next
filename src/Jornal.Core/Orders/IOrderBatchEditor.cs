namespace Jornal.Core.Orders;

/// <summary>
/// Обновляет только указанное поле выбранных заявок. При успехе возвращает все затронутые записи.
/// Весь пакет сохраняется в одной транзакции с проверкой исходного значения изменяемого поля.
/// При подтверждённом отказе пакет откатывается. Неопределённый исход commit обозначается явно.
/// </summary>
public interface IOrderBatchEditor
{
    Task<IReadOnlyList<OrderSummary>> SetColorAsync(IReadOnlyCollection<OrderSummary> expected, int color,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OrderSummary>> SetDeliveryNumberAsync(IReadOnlyCollection<OrderSummary> expected, string number,
        CancellationToken cancellationToken = default);
}

public static class OrderColors
{
    public const int Work = -882036;
    public const int Ready = -10238087;
    public const int Shipped = -8154114;
    public const int None = -1;
}
