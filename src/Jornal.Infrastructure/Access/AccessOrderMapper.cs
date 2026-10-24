using System.Globalization;
using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Access;

public static class AccessOrderMapper
{
    public static IReadOnlyList<string> Columns { get; } = Array.AsReadOnly(new[]
    {
        "Код", "Дата", "Менеджер", "Спецификация", "Заказчик", "Вид_резки", "Марка_стали", "Размер_листа",
        "Кол_во_листов", "Стоимость", "Толщина", "Ход", "Вес", "БЛЦБ", "Color", "Коэффициент"
    });

    public static void ValidateColumns(IEnumerable<string> columns)
    {
        var names = columns.ToHashSet(StringComparer.Ordinal);
        var missing = Columns.Where(name => !names.Contains(name)).ToArray();
        if (missing.Length > 0)
            throw new OrderReadException("Структура Orders отличается от ожидаемой. Отсутствуют поля: " + string.Join(", ", missing) + ".");
    }

    public static OrderSummary Map(IReadOnlyDictionary<string, object?> values)
    {
        var currentColumn = "Код";
        object? Get(string name)
        {
            currentColumn = name;
            var value = values[name];
            return value is DBNull ? null : value;
        }
        string Text(string name) => Convert.ToString(Get(name), CultureInfo.InvariantCulture) ?? "";
        decimal? Number(string name) => Get(name) is { } value
            ? value is string text ? decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)
                : Convert.ToDecimal(value, CultureInfo.InvariantCulture) : null;
        int? Integer(string name)
        {
            var number = Number(name);
            if (number is null) return null;
            if (decimal.Truncate(number.Value) != number.Value) throw new FormatException("Ожидалось целое число.");
            return checked((int)number.Value);
        }
        try
        {
            var id = Integer("Код") ?? throw new FormatException("Пустой ключ.");
            var date = Get("Дата") is { } rawDate ? rawDate is DateTime typedDate ? typedDate :
                DateTime.ParseExact((string)rawDate, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : (DateTime?)null;
            var order = new OrderSummary(id, date is { } actualDate ? DateOnly.FromDateTime(actualDate) : null,
                Text("Спецификация"), Text("Менеджер"), Text("Заказчик"), Text("Вид_резки"), Text("Марка_стали"),
                Number("Толщина"), Number("Стоимость"), Integer("Color"))
            {
                SheetSize = Text("Размер_листа"), SheetCount = Text("Кол_во_листов"), CutLengthMeters = Integer("Ход"),
                WeightKilograms = Text("Вес"), DeliveryNumber = Get("БЛЦБ") as string, StoredOrderDateTime = date,
                StoredCoefficient = Get("Коэффициент") as string
            };
            var parts = order.StoredCoefficient?.Split('|', StringSplitOptions.TrimEntries) ?? [];
            return order with
            {
                Coefficient = parts.Length > 0 ? OptionalDecimal(parts[0]) : null,
                PartCount = parts.Length > 1 ? OptionalCount(parts[1]) : null,
                PunchCount = parts.Length > 2 ? OptionalCount(parts[2]) : null,
                MaterialId = parts.Length > 3 && parts[3] != "-1" ? parts[3] : "",
                CuttingGas = parts.Length > 4 && parts[4] != "-1" ? parts[4] : ""
            };
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or InvalidCastException or KeyNotFoundException)
        {
            throw new OrderReadException($"Не удалось прочитать поле «{currentColumn}» в Orders. Проверьте формат данных; список не заменён.", exception);
        }
    }

    private static decimal? OptionalDecimal(string text) => decimal.TryParse(text.Replace(',', '.'),
        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) && value != -1 ? value : null;
    private static int? OptionalCount(string text) => int.TryParse(text, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
}
