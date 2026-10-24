using System.Globalization;

namespace Jornal.Workflow.Core;

public static class WorkflowOrderNumbers
{
    private const string Format = "yyyy-MM-dd-HHmmss";

    public static string Allocate(HashSet<string> used, DateTimeOffset at)
    {
        var stem = at.ToString(Format, CultureInfo.InvariantCulture);
        var number = stem;
        for (var sequence = 2; !used.Add(number); sequence++)
            number = stem + "-" + sequence.ToString("D2", CultureInfo.InvariantCulture);
        return number;
    }

    public static bool IsValid(string? number)
    {
        if (number is null || number.Length < 17 ||
            !DateTime.TryParseExact(number[..17], Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return false;
        return number.Length == 17 || number.Length >= 20 && number[17] == '-' &&
            int.TryParse(number.AsSpan(18), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) &&
            sequence >= 2 && number[18..] == sequence.ToString("D2", CultureInfo.InvariantCulture);
    }
}
