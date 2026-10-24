namespace Jornal.Core.Orders;

public enum OrderBatchField { Color, DeliveryNumber }

public sealed record OrderBatchChange(OrderBatchField Field, IReadOnlyList<OrderSummary> Expected, int Color, string? DeliveryNumber)
{
    public static OrderBatchChange ForColor(IReadOnlyCollection<OrderSummary> expected, int color) =>
        Create(OrderBatchField.Color, expected, color, null);

    public static OrderBatchChange ForDeliveryNumber(IReadOnlyCollection<OrderSummary> expected, string number)
    {
        ArgumentNullException.ThrowIfNull(number);
        if (number.Length > 255 || number.Any(char.IsControl) || number.Length > 0 && string.IsNullOrWhiteSpace(number))
            throw new ArgumentException("БЛЦБ должен занимать одну строку до 255 символов. Пустой номер очищает поле.", nameof(number));
        return Create(OrderBatchField.DeliveryNumber, expected, 0, number.Length == 0 ? null : number);
    }

    private static OrderBatchChange Create(OrderBatchField field, IReadOnlyCollection<OrderSummary> expected, int color, string? number)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (expected.Count == 0) throw new ArgumentException("Не выбраны заявки.", nameof(expected));
        var unique = new Dictionary<int, OrderSummary>();
        foreach (var row in expected)
        {
            if (unique.TryGetValue(row.Id, out var previous) &&
                (field == OrderBatchField.Color ? previous.Color != row.Color : previous.DeliveryNumber != row.DeliveryNumber))
                throw new ArgumentException("Для одной заявки переданы разные исходные значения.", nameof(expected));
            unique[row.Id] = row;
        }
        return new(field, unique.Values.OrderBy(row => row.Id).ToArray(), color, number);
    }

    public bool Matches(OrderSummary current, OrderSummary expected) => current.Id == expected.Id &&
        (Field == OrderBatchField.Color ? current.Color == expected.Color : current.DeliveryNumber == expected.DeliveryNumber);

    public OrderSummary Apply(OrderSummary current) => Field == OrderBatchField.Color
        ? current with { Color = Color } : current with { DeliveryNumber = DeliveryNumber };
}
