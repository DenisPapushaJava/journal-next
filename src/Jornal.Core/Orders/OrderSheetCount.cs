using System.Text.RegularExpressions;

namespace Jornal.Core.Orders;

// Ручной ввод; у HTML-импорта отдельно проверяется целый положительный итог.
public static partial class OrderSheetCount
{
    public const string ValidationMessage = "Укажите одно число.";

    public static bool IsValid(string? value) => value is { Length: > 0 and <= 255 } && Numeric().IsMatch(value);

    [GeneratedRegex(@"\A[+-]?[0-9]+(?:[.,][0-9]+)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Numeric();
}
