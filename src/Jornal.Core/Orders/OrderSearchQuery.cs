using System.Globalization;

namespace Jornal.Core.Orders;

public enum OrderSearchField { All, Specification, DeliveryNumber, Customer, Manager, Material, Comment }

// Подготовка выполняется один раз: слова по И либо целое значение выбранного поля.
public sealed class OrderSearchQuery
{
    private readonly (string Text, decimal? Thickness)[] _terms;
    public string Text { get; }
    public OrderSearchField Field { get; }
    public bool IsExactMatch { get; }

    public OrderSearchQuery(string? text, OrderSearchField field = OrderSearchField.All, bool exactMatch = false)
    {
        Field = Enum.IsDefined(field) ? field : OrderSearchField.All;
        IsExactMatch = exactMatch && Field != OrderSearchField.All;
        if (IsExactMatch)
        {
            Text = (text ?? "").Trim();
            _terms = [];
            return;
        }
        var words = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Text = string.Join(' ', words);
        _terms = words.Select(word => (word, decimal.TryParse(word.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var thickness) ? (decimal?)thickness : null)).ToArray();
    }

    public bool Matches(OrderSummary order)
    {
        if (IsExactMatch && Text.Length > 0)
            return string.Equals(FieldValue(order)?.Trim(), Text, StringComparison.OrdinalIgnoreCase);
        foreach (var term in _terms)
        {
            bool Contains(string? value) => value?.Contains(term.Text, StringComparison.OrdinalIgnoreCase) == true;
            var matches = Field != OrderSearchField.All ? Contains(FieldValue(order)) :
                    Contains(order.SpecificationNumber) || Contains(order.DeliveryNumber) || Contains(order.CustomerName)
                    || Contains(order.ManagerName) || Contains(order.MaterialName) || Contains(order.CuttingType)
                    || Contains(order.Comment) || term.Thickness is { } value && order.ThicknessMillimeters == value;
            if (!matches) return false;
        }
        return true;
    }

    private string? FieldValue(OrderSummary order) => Field switch
    {
        OrderSearchField.Specification => order.SpecificationNumber,
        OrderSearchField.DeliveryNumber => order.DeliveryNumber,
        OrderSearchField.Customer => order.CustomerName,
        OrderSearchField.Manager => order.ManagerName,
        OrderSearchField.Material => order.MaterialName,
        OrderSearchField.Comment => order.Comment,
        _ => null
    };
}
