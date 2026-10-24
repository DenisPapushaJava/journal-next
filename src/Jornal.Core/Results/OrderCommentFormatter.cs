using System.Globalization;
using System.Text;
using Jornal.Core.Orders;

namespace Jornal.Core.Results;

public static class OrderCommentFormatter
{
    private static readonly string[] LegacyOrder = ["Лазер", "HansLaser", "Плазма", "Газо-кислородная"];
    public static string Row(OrderSummary order)
    {
        RequireDeliveryNumbers([order]);
        return $"({Thickness(order)} мм)  {Line(order.SpecificationNumber)},  {Line(order.ManagerName)},  ({Line(order.CustomerName)}),  {Line(order.DeliveryNumber)}";
    }

    public static string Telegram(IReadOnlyList<OrderSummary> orders)
    {
        if (orders.Count == 0) throw new ArgumentException("Выберите заявки.");
        RequireSameDeliveryNumber(orders);
        if (orders.Any(order => order.AnnotationsUnavailable || order.AnnotationsNeedReview))
            throw new ArgumentException("Не удалось проверить пометки выбранных заявок. Проверьте гибку в редакторе или обновите список (F5).");
        var groups = orders.GroupBy(order => (Number: Line(order.DeliveryNumber), Manager: Line(order.ManagerName), Customer: Line(order.CustomerName))).ToArray();
        var result = new StringBuilder();
        foreach (var group in groups)
        {
            if (result.Length > 0 && result[^1] != '\n') result.Append("\n\n");
            var display = TelegramNumber(group.Key.Number);
            result.Append(display).Append("  ").Append(group.Key.Manager).Append(" (").Append(group.Key.Customer).Append(")\n");
            foreach (var machine in group.GroupBy(order => Line(order.CuttingType)).OrderBy(machine =>
                { var index = Array.IndexOf(LegacyOrder, machine.Key); return index < 0 ? int.MaxValue : index; }))
            {
                if (result.Length > 0 && result[^1] != '\n') result.Append("\n\n");
                foreach (var order in machine.OrderBy(order => !order.ThicknessMillimeters.HasValue)
                    .ThenBy(order => order.ThicknessMillimeters)
                    .ThenBy(order => Line(order.MaterialName), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(order => order.Id))
                    result.Append(Thickness(order)).Append(" мм ").Append(Line(order.MaterialName)).Append(order.HasBending ? " ГИБКА" : "").Append("  \n");
                result.Length--; // Прежний формат: подпись станка после последней строки группы.
                var caption = machine.Key switch { "Плазма" => "Плазму", "Газо-кислородная" => "Кристалл резка газом", _ => machine.Key.Length > 0 ? machine.Key : "Станок не указан" };
                result.Append(" на ").Append(caption);
                if (machine.Key != "Газо-кислородная") result.Append("\n\n");
            }
        }
        return result.ToString();
    }

    public static string Line(string? value) => string.Join(" ", new string((value ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Thickness(OrderSummary order) => order.ThicknessMillimeters?.ToString("0.############################", CultureInfo.GetCultureInfo("ru-RU")) ?? "";
    public static void RequireDeliveryNumbers(IReadOnlyList<OrderSummary> orders)
    {
        var missing = orders.Where(order => Line(order.DeliveryNumber).Length == 0).ToArray();
        if (missing.Length == 0) return;
        if (missing.Length == 1) throw new ArgumentException("Укажите БЛЦБ для заявки " + Label(missing[0]) + ".");
        var labels = string.Join(", ", missing.Take(5).Select(Label));
        var remaining = missing.Length > 5 ? $", ещё {missing.Length - 5}" : "";
        throw new ArgumentException($"Укажите БЛЦБ для заявок ({missing.Length}): {labels}{remaining}.");
    }
    public static void RequireSameDeliveryNumber(IReadOnlyList<OrderSummary> orders)
    {
        RequireDeliveryNumbers(orders);
        var groups = orders.GroupBy(order => Line(order.DeliveryNumber), StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
        if (groups.Length <= 1) return;
        var examples = groups.Take(3).Select(group =>
        {
            var runes = group.Key.EnumerateRunes().Take(33).ToArray();
            var number = runes.Length <= 32 ? group.Key : string.Concat(runes.Take(31)) + "…";
            return $"«{number}» — {Label(group.OrderBy(order => order.Id).First())}{(group.Count() > 1 ? $", ещё {group.Count() - 1}" : "")}";
        });
        var remaining = groups.Length > 3 ? $"; ещё БЛЦБ: {groups.Length - 3}" : "";
        throw new ArgumentException($"Разные БЛЦБ ({groups.Length}): {string.Join("; ", examples)}{remaining}. Выберите заявки с одним БЛЦБ.");
    }
    private static string Label(OrderSummary order)
    {
        var number = Line(order.SpecificationNumber);
        if (number.Length == 0) return $"код {order.Id}";
        var runes = number.EnumerateRunes().Take(49).ToArray();
        return runes.Length <= 48 ? number : string.Concat(runes.Take(40)) + $"… (код {order.Id})";
    }
    private static string TelegramNumber(string value)
    {
        var suffix = value.Length > 5 ? value[^5..] : value;
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit) ? suffix.TrimStart('0') is { Length: > 0 } number ? number : "0" : value;
    }
}
