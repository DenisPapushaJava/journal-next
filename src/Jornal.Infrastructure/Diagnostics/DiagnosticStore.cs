using System.IO.Compression;
using System.Text.Json;

namespace Jornal.Infrastructure.Diagnostics;

public sealed record PendingDiagnostic(DiagnosticReport Report, int Attempts, DateTimeOffset NextAttemptUtc);
public sealed record DiagnosticSettings(int Schema = 1, bool SendAutomatically = true);

public sealed class DiagnosticStore(string root)
{
    public const int MaximumReports = 200;
    public const int MaximumPending = 100;
    public static TimeSpan Retention { get; } = TimeSpan.FromDays(14);
    private readonly object _gate = new();
    public string Root => root;
    private string LogDirectory => Path.Combine(root, "events");
    private string QueueDirectory => Path.Combine(root, "outbox");
    private string SettingsPath => Path.Combine(root, "settings.json");
    public bool SettingsUnreadable { get; private set; }

    public bool LoadAutomatic()
    {
        lock (_gate)
        {
            try
            {
                using var stream = File.OpenRead(SettingsPath);
                if (stream.Length > 4096) throw new InvalidDataException();
                using var document = JsonDocument.Parse(stream);
                var value = document.RootElement;
                if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 2 ||
                    !value.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var number) || number != 1 ||
                    !value.TryGetProperty("sendAutomatically", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException();
                SettingsUnreadable = false;
                return enabled.GetBoolean();
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            { SettingsUnreadable = false; return true; }
            catch (Exception) { SettingsUnreadable = true; return false; } // Повреждённый выбор не трактуем как согласие на отправку.
        }
    }
    public bool SaveAutomatic(bool enabled)
    {
        lock (_gate)
        {
            try { Write(SettingsPath, new DiagnosticSettings(SendAutomatically: enabled)); SettingsUnreadable = false; return true; }
            catch (Exception) { return false; }
        }
    }
    public bool Save(DiagnosticReport report, bool enqueue)
    {
        lock (_gate)
        {
            try
            {
                report.Validate();
                Write(Path.Combine(LogDirectory, report.EventId + ".json"), report);
                if (enqueue) Write(Path.Combine(QueueDirectory, report.EventId + ".json"), new PendingDiagnostic(report, 0, report.CreatedUtc));
                Trim(LogDirectory, MaximumReports); Trim(QueueDirectory, MaximumPending);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
    public PendingDiagnostic[] Due(DateTimeOffset now)
    {
        lock (_gate)
        {
            try
            {
                Trim(QueueDirectory, MaximumPending);
                return Files(QueueDirectory).Select(ReadPending).OfType<PendingDiagnostic>()
                    .Where(item => item.NextAttemptUtc <= now).OrderBy(item => item.Report.CreatedUtc).Take(5).ToArray();
            }
            catch (Exception) { return []; }
        }
    }
    private PendingDiagnostic? ReadPending(string path)
    {
        try
        {
            var item = Read<PendingDiagnostic>(path, DiagnosticReport.MaximumBytes + 4096);
            item.Report.Validate();
            if (item.Attempts is < 0 or > 12 || Path.GetFileNameWithoutExtension(path) != item.Report.EventId ||
                item.Report.CreatedUtc < DateTimeOffset.UtcNow - Retention || item.Report.CreatedUtc > DateTimeOffset.UtcNow.AddDays(1)) throw new InvalidDataException();
            return item;
        }
        catch (Exception) { TryDelete(path); return null; }
    }
    public void Complete(string id) { lock (_gate) TryDelete(Path.Combine(QueueDirectory, Id(id) + ".json")); }
    public void Retry(PendingDiagnostic item, DateTimeOffset now, TimeSpan? retryAfter = null)
    {
        lock (_gate)
        {
            var path = Path.Combine(QueueDirectory, Id(item.Report.EventId) + ".json");
            if (item.Attempts >= 11) { TryDelete(path); return; }
            var delay = retryAfter ?? TimeSpan.FromMinutes(Math.Min(360, Math.Pow(2, item.Attempts))) * (1 + Random.Shared.NextDouble() * 0.2);
            delay = TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 60, 6 * 60 * 60));
            try { Write(path, item with { Attempts = item.Attempts + 1, NextAttemptUtc = now + delay }); } catch (Exception) { }
        }
    }
    public void ClearQueue() { lock (_gate) foreach (var path in Files(QueueDirectory)) TryDelete(path); }
    public int PendingCount { get { lock (_gate) try { return Files(QueueDirectory).Length; } catch (Exception) { return 0; } } }
    public bool HasFatal
    {
        get
        {
            lock (_gate) try { return LatestFatal() is { } latest &&
                (!File.Exists(Path.Combine(root, "fatal-seen.json")) || Read<string>(Path.Combine(root, "fatal-seen.json"), 100) != latest.EventId); }
            catch (Exception) { return false; }
        }
    }
    public void AcknowledgeFatal()
    {
        lock (_gate) try
        { if (LatestFatal() is { } latest) Write(Path.Combine(root, "fatal-seen.json"), latest.EventId); }
        catch (Exception) { }
    }
    private DiagnosticReport? LatestFatal() => Files(LogDirectory).Select(path =>
    {
        try { var report = Read<DiagnosticReport>(path, DiagnosticReport.MaximumBytes); report.Validate(); return report; }
        catch (Exception) { return null; }
    }).Where(report => report is { Severity: DiagnosticSeverity.Fatal } && report.CreatedUtc >= DateTimeOffset.UtcNow - Retention)
        .OrderByDescending(report => report!.CreatedUtc).FirstOrDefault();
    public void Export(Stream destination)
    {
        lock (_gate)
        {
            using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var path in Files(LogDirectory).OrderDescending().Take(MaximumReports))
            {
                DiagnosticReport report;
                try { report = Read<DiagnosticReport>(path, DiagnosticReport.MaximumBytes); report.Validate(); } catch (Exception) { continue; }
                using var stream = archive.CreateEntry("events/" + report.EventId + ".json", CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(stream, report, DiagnosticReport.Json); // Не копируем произвольные файлы/лишние свойства из профиля.
            }
        }
    }
    private static string Id(string value) => Guid.TryParseExact(value, "N", out _) ? value : throw new InvalidDataException();
    private static string[] Files(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory, "*.json") : [];
    private static T Read<T>(string path, int maximum)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > maximum) throw new InvalidDataException();
        return JsonSerializer.Deserialize<T>(stream, DiagnosticReport.Json) ?? throw new InvalidDataException();
    }
    private static void Write<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, DiagnosticReport.Json);
        if (bytes.Length > DiagnosticReport.MaximumBytes + 4096) throw new InvalidDataException();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }
    private static void Trim(string directory, int maximum)
    {
        if (Directory.Exists(directory))
            foreach (var path in Directory.GetFiles(directory, "*.tmp").Where(path => File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))) TryDelete(path);
        var files = Files(directory).Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).ToArray();
        foreach (var file in files.Where((file, index) => index >= maximum || file.CreationTimeUtc < DateTime.UtcNow - Retention)) TryDelete(file.FullName);
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception) { } }
}
