using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jornal.Parsing.Documents;
using Jornal.Parsing.Specifications;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed record ReportFileSnapshot(string SourcePath, string Sha256, byte[] Bytes, SpecificationParseResult Parsed);

public static partial class WorkflowReportFiles
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    public static bool IsReport(string path) => Path.GetExtension(path).ToLowerInvariant() is ".htm" or ".html" or ".pdf";

    public static async Task<ReportFileSnapshot> ReadAsync(string path, CancellationToken token = default)
    {
        if (!IsReport(path)) throw new WorkflowValidationException("Выберите HTML или PDF-спецификацию.");
        var bytes = await CaptureAsync(path, token);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
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

    public static SpecificationParseResult Parse(SourceDocument document, CancellationToken token = default)
    {
        // The legacy AWJC parser accepts 3–4 digits. Adapt only the new Workflow input and restore the complete identifier.
        var replacements = new List<(int Page, PdfWord Word, string Short, string Full)>();
        string Shorten(PdfWord word, int page) => AwjcSeconds().Replace(word.Text, match =>
        {
            var shortId = match.Groups["date"].Value + "-" + match.Groups["time"].Value[..4];
            replacements.Add((page, word, shortId, match.Groups["date"].Value + "-" + match.Groups["time"].Value));
            return shortId;
        });
        var normalized = document with { Pages = document.Pages.Select(page => page with
        { Words = page.Words.Select(word => word with { Text = Shorten(word, page.Number) }).ToArray() }).ToArray() };
        var report = SpecificationProfileCatalog.Default.Parse(normalized, token);
        if (report.Profile == WaterjetPdfSpecificationParser.Profile && report.Header is { } header)
        {
            var identifiers = replacements.Where(change => change.Short == header.Value.OriginalIdentifier &&
                header.Sources.OfType<PdfValueSource>().Any(source => source.Page == change.Page &&
                    change.Word.Left >= source.Left - 0.1 && change.Word.Right <= source.Right + 0.1 &&
                    change.Word.Bottom >= source.Bottom - 0.1 && change.Word.Top <= source.Top + 0.1))
                .Select(change => change.Full).Distinct().ToArray();
            if (identifiers.Length > 1) throw new WorkflowValidationException("В заголовке AWJC несколько имён проектов.");
            if (identifiers.SingleOrDefault() is { } full)
                report = report with { Header = header with { Value = header.Value with
                { OriginalIdentifier = full, JournalNumber = header.Value.OrderDate.ToString("MMdd") + "-" + full[^6..],
                    OrderName = header.Value.OrderName.Replace(header.Value.OriginalIdentifier, full, StringComparison.Ordinal) } } };
        }
        return report;
    }

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
        return Regex.IsMatch(header.Value.OrderName, @"(?<![\p{L}\p{N}])" + Regex.Escape(name) + @"(?=$|[\s""'])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static async Task RevalidateAsync(ReportFileSnapshot report, CancellationToken token = default)
    {
        var bytes = await CaptureAsync(report.SourcePath, token);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != report.Sha256)
            throw new WorkflowValidationException("Отчёт изменился после подбора. Обновите подбор; введённые правки сохранены в окне.");
    }

    public static async Task VerifyArchiveAsync(AcceptedReport report, CancellationToken token = default)
    {
        report.Validate();
        if (Convert.ToHexString(SHA256.HashData(await CaptureAsync(report.ArchivePath, token))) != report.Sha256)
            throw new WorkflowValidationException("Принятая копия отчёта изменена или повреждена.");
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

    [GeneratedRegex(@"(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})-(?<time>[0-9]{6})(?=--AWJC)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AwjcSeconds();
}
