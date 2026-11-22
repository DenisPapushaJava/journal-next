namespace Jornal.Workflow.Core;

// Parsed values, before manual editing. Incomplete values stay empty; they are never invented as zero.
public sealed record ReportValues(string DocumentNumber, string DocumentDate, string Material, string MaterialGroup,
    string Thickness, string BlankSize, string Quantity, string Length, string Pierces, string Weight, string Duration);

public sealed record AcceptedReport(Guid Id, string Sha256, long Length, string FileName, string ArchivePath,
    ReportValues Parsed)
{
    public void Validate()
    {
        if (Id == Guid.Empty || Sha256 is not { Length: 64 } || !Sha256.All(char.IsAsciiHexDigit) || Length <= 0 ||
            string.IsNullOrWhiteSpace(FileName) || !IsArchiveLocation(ArchivePath) || Parsed is null)
            throw new WorkflowValidationException("Снимок исходного отчёта повреждён.");
    }

    private static bool IsArchiveLocation(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Any(char.IsControl)) return false;
        if (Path.IsPathFullyQualified(path)) return true;

        // A remote snapshot keeps the original path from the Windows server.
        var location = path.AsSpan();
        if (location.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return HasServerAndShare(location[8..]);
        if (location.StartsWith(@"\\?\", StringComparison.Ordinal)) location = location[4..];
        else if (location.StartsWith(@"\\", StringComparison.Ordinal)) return HasServerAndShare(location[2..]);
        return location.Length >= 3 && char.IsAsciiLetter(location[0]) && location[1] == ':' && location[2] is '\\' or '/';
    }

    private static bool HasServerAndShare(ReadOnlySpan<char> location)
    {
        var serverEnd = location.IndexOfAny('\\', '/');
        return serverEnd > 0 && serverEnd + 1 < location.Length && location[..serverEnd] is not ("." or "?") &&
            location[serverEnd + 1] is not ('\\' or '/');
    }
}
