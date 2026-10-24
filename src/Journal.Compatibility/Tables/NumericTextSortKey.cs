using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Jornal.Desktop.Tables;

// Ключ только для сортировки прежних текстовых полей MDB, без округления и записи обратно.
public sealed partial class NumericTextSortKey : IComparable<NumericTextSortKey>, IComparable
{
    private readonly BigInteger _coefficient;
    private readonly int _scale;

    private NumericTextSortKey(BigInteger coefficient, int scale) => (_coefficient, _scale) = (coefficient, scale);

    public static NumericTextSortKey? Parse(string? source)
    {
        var text = source?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 255 || !Number().IsMatch(text)) return null;
        var separator = text.IndexOfAny(['.', ',']);
        var scale = separator < 0 ? 0 : text.Length - separator - 1;
        var coefficient = BigInteger.Parse(text.Replace(".", "").Replace(",", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return new(coefficient, scale);
    }

    public int CompareTo(NumericTextSortKey? other)
    {
        if (other is null) return 1;
        if (_scale == other._scale) return _coefficient.CompareTo(other._coefficient);
        return _scale > other._scale
            ? _coefficient.CompareTo(other._coefficient * BigInteger.Pow(10, _scale - other._scale))
            : (_coefficient * BigInteger.Pow(10, other._scale - _scale)).CompareTo(other._coefficient);
    }

    int IComparable.CompareTo(object? value) => value is null ? 1 : value is NumericTextSortKey number
        ? CompareTo(number) : throw new ArgumentException("Ожидался числовой ключ сортировки.", nameof(value));

    [GeneratedRegex(@"\A[+-]?[0-9]+(?:[.,][0-9]+)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Number();
}
