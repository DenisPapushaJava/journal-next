namespace Jornal.Desktop.Tables;

// Числовые части номера сравниваются без преобразования во время и без ограничения Int32.
// Исходный номер остаётся прежним; ведущие нули не меняют его место в сортировке.
public sealed class SpecificationNumberSortKey(string? text) : IComparable<SpecificationNumberSortKey>, IComparable
{
    private readonly string _text = text?.Trim() ?? "";

    public int CompareTo(SpecificationNumberSortKey? other)
    {
        if (other is null) return 1;
        var left = 0;
        var right = 0;
        while (left < _text.Length && right < other._text.Length)
        {
            if (IsDigit(_text[left]) && IsDigit(other._text[right]))
            {
                var leftEnd = left;
                var rightEnd = right;
                while (leftEnd < _text.Length && IsDigit(_text[leftEnd])) leftEnd++;
                while (rightEnd < other._text.Length && IsDigit(other._text[rightEnd])) rightEnd++;
                while (left < leftEnd && _text[left] == '0') left++;
                while (right < rightEnd && other._text[right] == '0') right++;
                var comparison = (leftEnd - left).CompareTo(rightEnd - right);
                if (comparison != 0) return comparison;
                comparison = _text.AsSpan(left, leftEnd - left).SequenceCompareTo(other._text.AsSpan(right, rightEnd - right));
                if (comparison != 0) return comparison;
                left = leftEnd;
                right = rightEnd;
            }
            else
            {
                var comparison = char.ToUpperInvariant(_text[left]).CompareTo(char.ToUpperInvariant(other._text[right]));
                if (comparison != 0) return comparison;
                left++;
                right++;
            }
        }
        return (_text.Length - left).CompareTo(other._text.Length - right);
    }

    int IComparable.CompareTo(object? value) => value is null ? 1 : value is SpecificationNumberSortKey number
        ? CompareTo(number) : throw new ArgumentException("Ожидался номер спецификации.", nameof(value));

    private static bool IsDigit(char value) => value is >= '0' and <= '9';
}
