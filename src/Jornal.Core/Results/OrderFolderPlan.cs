using System.Text;
using Jornal.Core.Orders;

namespace Jornal.Core.Results;

public sealed record OrderFolderItem(int OrderId, string OriginalName, string Name)
{
    public bool Changed => OriginalName != Name;
}
public sealed record OrderFolderPlan(IReadOnlyList<OrderFolderItem> Items)
{
    public const int MaximumFolders = 1000;
    public const int MaximumNameLength = 140;
    private const string Invalid = "<>:\"/\\|?*";
    public static OrderFolderPlan Create(IReadOnlyList<OrderSummary> orders, int nameLimit = MaximumNameLength)
    {
        if (nameLimit is < 40 or > MaximumNameLength) throw new ArgumentException("Слишком длинный путь к временным файлам.");
        if (orders.Count is < 1 or > MaximumFolders) throw new ArgumentException($"Выберите от 1 до {MaximumFolders} заявок.");
        OrderCommentFormatter.RequireSameDeliveryNumber(orders);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var result = new List<OrderFolderItem>();
        foreach (var order in orders)
        {
            var original = OrderCommentFormatter.Row(order); var stem = SafeName(original, nameLimit);
            var name = stem; var count = 1;
            while (!used.Add(name)) { var suffix = $" ({++count})"; name = SafeName(original, nameLimit - suffix.Length) + suffix; }
            result.Add(new(order.Id, original, name));
        }
        return new(result);
    }
    public static string SafeName(string value, int limit = MaximumNameLength)
    {
        if (limit is < 1 or > MaximumNameLength) throw new ArgumentOutOfRangeException(nameof(limit));
        var clean = new string(value.Select(c => char.IsControl(c) || Invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length == 0) clean = "Заявка";
        var device = clean.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] is >= '1' and <= '9' or '¹' or '²' or '³') clean = "_" + clean;
        if (clean.Length <= limit) return clean;
        var tail = Math.Min(40, limit / 3); var start = clean.Length - tail;
        if (char.IsLowSurrogate(clean[start])) start++;
        return (Cut(clean, limit - (clean.Length - start) - 1) + "…" + clean[start..]).TrimEnd(' ', '.');
    }
    private static string Cut(string value, int limit)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes()) { if (result.Length + rune.Utf16SequenceLength > limit) break; result.Append(rune); }
        return result.ToString();
    }
}
