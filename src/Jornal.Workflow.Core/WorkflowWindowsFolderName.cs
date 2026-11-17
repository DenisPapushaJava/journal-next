namespace Jornal.Workflow.Core;

public static class WorkflowWindowsFolderName
{
    private const string Forbidden = "<>:\"/\\|?*";

    public static bool IsInvalidCharacter(char value) => char.IsControl(value) || Forbidden.Contains(value);

    public static bool IsValid(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 255 &&
        !name.Any(IsInvalidCharacter) && !name.EndsWith(' ') && !name.EndsWith('.') && !IsReserved(name);

    public static bool IsReserved(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }
}
