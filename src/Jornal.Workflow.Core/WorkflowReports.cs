namespace Jornal.Workflow.Core;

// Parsed values, before manual editing. Incomplete values stay empty; they are never invented as zero.
public sealed record ReportValues(string DocumentNumber, string DocumentDate, string Material, string MaterialGroup,
    string Thickness, string BlankSize, string Quantity, string Length, string Pierces, string Weight, string Duration);

public sealed record AcceptedReport(Guid Id, string Sha256, long Length, string FileName, string ArchivePath,
    ReportValues Parsed)
{
    public void Validate()
    {
        if (Id == Guid.Empty || Sha256.Length != 64 || !Sha256.All(char.IsAsciiHexDigit) || Length <= 0 ||
            string.IsNullOrWhiteSpace(FileName) || !Path.IsPathFullyQualified(ArchivePath) || Parsed is null)
            throw new WorkflowValidationException("Снимок исходного отчёта повреждён.");
    }
}
