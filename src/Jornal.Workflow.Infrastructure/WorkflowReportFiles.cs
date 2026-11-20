using System.Security.Cryptography;
using System.Globalization;
using System.Text.RegularExpressions;
using Jornal.Parsing.Documents;
using Jornal.Parsing.Specifications;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed record ReportFileSnapshot(string SourcePath, string Sha256, byte[] Bytes, SpecificationParseResult Parsed);
public readonly record struct ReportFileCandidate(string Path, string? LegacyBundleReceipt = null);

public static partial class WorkflowReportFiles
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly SpecificationProfileCatalog Profiles = new(SpecificationProfileCatalog.Default.Profiles.Select(profile => profile switch
    {
        HansPdfSpecificationParser => new HansPdfSpecificationParser(allowTruncatedProjectName: true, enforceLegacySheetSizeLimit: false),
        WaterjetPdfSpecificationParser => new WaterjetPdfSpecificationParser(useExplicitCalculationInputs: true),
        _ => profile
    }));
    public static bool IsReport(string path) => Path.GetExtension(path).ToLowerInvariant() is ".htm" or ".html" or ".pdf";
    public static IEnumerable<ReportFileCandidate> ListReports(string folder, string? profile)
    {
        var markers = ShopFileSystem.ManagerMapMarkers(folder);
        foreach (var path in Directory.EnumerateFiles(folder).Where(path => IsReport(path, profile)))
        {
            var name = Path.GetFileName(path);
            if (markers?.Contains(name) != true) yield return new(path);
            else if (profile == "hans-pdf/v1" && !Path.GetFileNameWithoutExtension(name)
                .EndsWith(WorkflowManagerMapBundle.PreviousFileSuffix, StringComparison.OrdinalIgnoreCase))
                // Older bundles could use the specification's exact name. Check the current bytes.
                yield return new(path, Path.Combine(folder, WorkflowManagerMapBundle.MarkerFolder, name));
        }
    }

    // BLOK specifications are HTML; adjacent PDF cutting maps are a different document.
    // AWJC exports cutting maps with a _pp suffix and specifications separately.
    public static bool IsReport(string path, string? profile) => profile switch
    {
        "block-html/v1" => Path.GetExtension(path).ToLowerInvariant() is ".htm" or ".html",
        "hans-pdf/v1" => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(path).EndsWith(WorkflowManagerMapBundle.FileSuffix, StringComparison.OrdinalIgnoreCase),
        WaterjetPdfSpecificationParser.Profile => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileNameWithoutExtension(path).EndsWith("_pp", StringComparison.OrdinalIgnoreCase),
        _ => IsReport(path)
    };

    public static async Task<ReportFileSnapshot> ReadAsync(string path, CancellationToken token = default) =>
        (await ReadAsync(new ReportFileCandidate(path), token))!;

    public static async Task<ReportFileSnapshot?> ReadAsync(ReportFileCandidate candidate, CancellationToken token = default)
    {
        var path = candidate.Path;
        if (!IsReport(path)) throw new WorkflowValidationException("Выберите HTML или PDF-спецификацию.");
        var bytes = await CaptureAsync(path, token);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (candidate.LegacyBundleReceipt is { } receipt && await WorkflowManagerMapBundle.IsRecordedBundleAsync(receipt, hash, token))
            return null;
        // The parser and hash see exactly the same bytes. DocumentReader remains unchanged for the old Journal.
        var temporary = Path.Combine(Path.GetTempPath(), "workflow-report-" + Guid.NewGuid().ToString("N") + Path.GetExtension(path));
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            var document = await new DocumentReader().ReadAsync(temporary, token);
            var parsed = Parse(document, token);
            return new(Path.GetFullPath(path), hash, bytes, parsed);
        }
        finally { File.Delete(temporary); }
    }

    public static SpecificationParseResult Parse(SourceDocument document, CancellationToken token = default) => Profiles.Parse(document, token);

    public static bool Matches(ReportFileSnapshot report, CuttingProject project, WorkflowSpecification? existing, WorkflowMachine? machine = null)
    {
        if (report.Parsed.Profile != project.ImportProfile || report.Parsed.Header is not { } header) return false;
        if (project.NativePath is null)
            return existing?.Current.SourcePath is { } original &&
                string.Equals(Path.GetFullPath(original), report.SourcePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                existing.Current.DocumentNumber == header.Value.JournalNumber &&
                (report.Parsed.Profile != "block-html/v1" || machine is not null &&
                    string.Equals(report.Parsed.CuttingType?.Value, WorkflowProjectNames.Tag(machine), StringComparison.OrdinalIgnoreCase));
        var name = Path.GetFileName(project.NativePath);
        if (Regex.IsMatch(header.Value.OrderName, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?=$|[\s""'])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return true;
        return report.Parsed.Profile switch
        {
            HansPdfSpecificationParser.Profile => MatchesHansProjectName(report.Parsed, header.Value, project, name),
            WaterjetPdfSpecificationParser.Profile => MatchesAwjcName(report.Parsed, header.Value, project, name),
            _ => false
        };
    }

    private static bool MatchesAwjcName(SpecificationParseResult report, SpecificationHeader header, CuttingProject project, string name)
    {
        if (!report.IsRecognized || report.Dimensions?.Value.ThicknessMillimeters != project.ThicknessMm) return false;
        var expected = AwjcName().Match(Regex.Replace(name, @"\s+", ""));
        var actual = AwjcName().Match(Regex.Replace(header.OrderName, @"\s+", ""));
        if (!expected.Success || !actual.Success || !name.EndsWith(".dig", StringComparison.OrdinalIgnoreCase) ||
            expected.Groups["id"].Value != header.OriginalIdentifier || actual.Groups["id"].Value != header.OriginalIdentifier)
            return false;
        var thickness = actual.Groups["thickness"];
        return !thickness.Success || decimal.TryParse(thickness.Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var reportedThickness) && reportedThickness == project.ThicknessMm;
    }

    private static bool MatchesHansProjectName(SpecificationParseResult report, SpecificationHeader header, CuttingProject project, string name)
    {
        if (!report.IsRecognized ||
            report.Dimensions is { } dimensions && dimensions.Value.ThicknessMillimeters != project.ThicknessMm ||
            report.SheetLayouts.Any(sheet => sheet.Value.Dimensions.ThicknessMillimeters != project.ThicknessMm)) return false;

        var reportedName = Regex.Replace(header.OrderName.Trim(), @"\s+", " ");
        var projectName = Regex.Replace(name.Trim(), @"\s+", " ");
        if (projectName.Equals(reportedName, StringComparison.OrdinalIgnoreCase)) return true;

        var identifier = header.OriginalIdentifier;
        var expectedTail = identifier + "--HANSLASER.Dsp";
        if (identifier.Length != 17 || !projectName.EndsWith(" " + expectedTail, StringComparison.OrdinalIgnoreCase) ||
            !reportedName.Contains(identifier, StringComparison.Ordinal)) return false;

        return projectName.StartsWith(reportedName, StringComparison.OrdinalIgnoreCase) ||
            (report.Dimensions is not null || report.SheetLayouts.Count > 0) &&
            expectedTail.StartsWith(reportedName, StringComparison.OrdinalIgnoreCase);
    }

    public static async Task RevalidateAsync(ReportFileSnapshot report, CancellationToken token = default)
    {
        var bytes = await CaptureAsync(report.SourcePath, token);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != report.Sha256)
            throw new WorkflowValidationException("Отчёт изменился после подбора. Обновите подбор; введённые правки сохранены в окне.");
    }

    public static async Task VerifyArchiveAsync(AcceptedReport report, CancellationToken token = default)
        => _ = await ReadVerifiedArchiveAsync(report, token);

    public static Task<string> PrepareArchiveForOpeningAsync(AcceptedReport report, CancellationToken token = default)
        => PrepareArchiveForOpeningAsync(report, OperatingSystem.IsWindows(), Path.GetTempPath(), token);

    internal static async Task<string> PrepareArchiveForOpeningAsync(AcceptedReport report, bool windows, string tempRoot, CancellationToken token)
    {
        var bytes = await ReadVerifiedArchiveAsync(report, token);
        // Windows viewers can reject a long path even when .NET can read the file.
        if (!windows || report.ArchivePath.Length < 260 ||
            !Path.GetExtension(report.ArchivePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return report.ArchivePath;

        var folder = Path.Combine(tempRoot, "JournalNext-Pdf");
        Directory.CreateDirectory(folder);
        RemoveExpiredPreviews(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".pdf");
        token.ThrowIfCancellationRequested();
        var created = false;
        try
        {
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                created = true;
                await output.WriteAsync(bytes, token);
            }
            return path;
        }
        catch when (created)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void RemoveExpiredPreviews(string folder)
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.pdf"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _)) continue;
                try { if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static async Task<byte[]> ReadVerifiedArchiveAsync(AcceptedReport report, CancellationToken token)
    {
        report.Validate();
        var bytes = await CaptureAsync(report.ArchivePath, token);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != report.Sha256)
            throw new WorkflowValidationException("Принятая копия отчёта изменена или повреждена.");
        return bytes;
    }

    public static async Task<AcceptedReport> ArchiveAsync(ReportFileSnapshot report, ReportValues baseline, CancellationToken token = default)
    {
        if (Convert.ToHexString(SHA256.HashData(report.Bytes)) != report.Sha256)
            throw new WorkflowValidationException("Захваченная копия отчёта повреждена. Повторите подбор.");
        var archiveRoot = Path.Combine(Path.GetDirectoryName(report.SourcePath)!, ".workflow-reports");
        var folder = Path.Combine(archiveRoot, report.Sha256);
        Directory.CreateDirectory(folder);
        WorkflowServiceFiles.TryHide(archiveRoot);
        var path = Path.Combine(folder, Path.GetFileName(report.SourcePath));
        var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await output.WriteAsync(report.Bytes, token); output.Flush(true); }
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                // Existing snapshots are reusable only when their bytes really match.
                if (Convert.ToHexString(SHA256.HashData(await CaptureAsync(path, token))) != report.Sha256)
                    throw new WorkflowValidationException("Архив отчёта повреждён. Прежний расчёт сохранён.");
            }
        }
        finally { File.Delete(temporary); }
        return new(Guid.NewGuid(), report.Sha256, report.Bytes.LongLength, Path.GetFileName(report.SourcePath), path, baseline);
    }

    private static async Task<byte[]> CaptureAsync(string path, CancellationToken token)
    {
        static async Task<byte[]> Read(string path, CancellationToken token)
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            if (input.Length is <= 0 or > MaximumBytes) throw new WorkflowValidationException("Отчёт пуст или больше 32 МБ. Дождитесь окончания сохранения.");
            var bytes = new byte[(int)input.Length]; await input.ReadExactlyAsync(bytes, token);
            if (input.Length != bytes.Length) throw new WorkflowValidationException("Отчёт ещё сохраняется. Повторите подбор позже.");
            return bytes;
        }
        var first = await Read(path, token); var second = await Read(path, token);
        if (!SHA256.HashData(first).AsSpan().SequenceEqual(SHA256.HashData(second)))
            throw new WorkflowValidationException("Отчёт ещё сохраняется. Повторите подбор позже.");
        return first;
    }

    [GeneratedRegex(@"\A(?:(?<thickness>[0-9]+(?:[.,][0-9]+)?)(?:mm|мм))?(?<id>[0-9]{4}-[0-9]{2}-[0-9]{2}-{1,2}(?:[0-9]{6}|[0-9]{3,4}))--(?:AWJC|ГАР)(?:\.(?:d(?:i(?:g)?)?)?)?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AwjcName();
}
