using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

// Reports remain in the existing immutable .workflow-reports archive. These small
// snapshots keep calculation inputs and prices, without adding history to MDB reads.
public sealed record CalculationDraftSnapshot(ReportValues Values, string Coefficient, bool HasBending,
    bool HasChamfer, string? Comment, string PricePerMeter, string PricePerPierce,
    CuttingCalculation? Calculation, AcceptedReport? Report);
public sealed record CalculationHistorySnapshot(int Schema, Guid OrderId, Guid ProjectId,
    DateTimeOffset CapturedAt, Guid CapturedBy, SpecificationState? State, CalculationDraftSnapshot? Draft)
{
    public long OrderVersion { get; init; }
}
public sealed record CalculationHistoryRead(CalculationHistorySnapshot[] Snapshots, string? Warning);

public static class WorkflowCalculationHistory
{
    public const string FolderName = ".workflow-history";
    private const int MaximumSnapshotBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Json = new();

    private static string Folder(string projectFolder, Guid projectId)
    {
        if (!Path.IsPathFullyQualified(projectFolder) || projectId == Guid.Empty)
            throw new WorkflowValidationException("У расчёта не задана папка проекта для сохранения предыдущего состояния.");
        var path = Path.Combine(projectFolder, FolderName, projectId.ToString("N"));
        ShopFileSystem.RequireOrdinaryPath(path);
        return path;
    }

    public static async Task SaveAsync(string projectFolder, CalculationHistorySnapshot snapshot, CancellationToken token = default)
    {
        Validate(snapshot);
        var folder = Folder(projectFolder, snapshot.ProjectId);
        // Timestamp/capturing user do not create duplicates on a retry. SavedAt and
        // author in State still distinguish a later return to the same inputs.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Json);
        var identity = Identity(bytes);
        var key = Convert.ToHexString(SHA256.HashData(identity));
        if (bytes.Length > MaximumSnapshotBytes) throw new WorkflowValidationException("Предыдущий расчёт слишком велик для сохранения в историю.");
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(folder); WorkflowServiceFiles.TryHide(Path.GetDirectoryName(folder)!);
        var path = Path.Combine(folder, key + ".json"); ShopFileSystem.RequireOrdinaryPath(path);
        if ((snapshot.Draft?.Report ?? snapshot.State?.Report) is { } report)
            await WorkflowReportFiles.VerifyArchiveAsync(report, token);
        if (File.Exists(path))
        {
            var existing = await ReadSnapshotAsync(path, token);
            if (existing.OrderId != snapshot.OrderId || existing.ProjectId != snapshot.ProjectId ||
                !Identity(JsonSerializer.SerializeToUtf8Bytes(existing, Json)).AsSpan().SequenceEqual(identity))
                throw new WorkflowValidationException("Предыдущий расчёт в истории повреждён. Текущий расчёт не заменён.");
            return;
        }
        var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            { await output.WriteAsync(bytes, token); output.Flush(true); }
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path))
            {
                // Another client may have preserved this same state first.
                var existing = await ReadSnapshotAsync(path, token);
                if (existing.OrderId != snapshot.OrderId || existing.ProjectId != snapshot.ProjectId ||
                    !Identity(JsonSerializer.SerializeToUtf8Bytes(existing, Json)).AsSpan().SequenceEqual(identity))
                    throw new WorkflowValidationException("Не удалось проверить предыдущий расчёт. Текущий расчёт не заменён.");
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<CalculationHistoryRead> ReadAsync(string projectFolder, Guid orderId, Guid projectId, CancellationToken token = default)
    {
        var folder = Folder(projectFolder, projectId);
        if (!Directory.Exists(folder)) return new([], null);
        var files = Directory.EnumerateFiles(folder, "*.json").Take(2001).ToArray();
        if (files.Length > 2000) throw new WorkflowValidationException("В истории проекта больше 2000 состояний. Откройте служебную папку для проверки архива.");
        var snapshots = new List<CalculationHistorySnapshot>(); var unavailable = 0;
        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var snapshot = await ReadSnapshotAsync(path, token);
                if (snapshot.OrderId != orderId || snapshot.ProjectId != projectId) throw new InvalidDataException();
                snapshots.Add(snapshot);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or WorkflowValidationException)
            { unavailable++; }
        }
        return new(snapshots.ToArray(), unavailable == 0 ? null : $"Не удалось прочитать состояний: {unavailable}. Остальная история доступна; файлы сохранены.");
    }

    private static async Task<CalculationHistorySnapshot> ReadSnapshotAsync(string path, CancellationToken token)
    {
        ShopFileSystem.RequireOrdinaryPath(path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length is <= 0 or > MaximumSnapshotBytes) throw new InvalidDataException();
        var bytes = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes, token);
        var snapshot = JsonSerializer.Deserialize<CalculationHistorySnapshot>(bytes) ?? throw new InvalidDataException();
        Validate(snapshot);
        var identity = Identity(bytes);
        if (!Path.GetFileNameWithoutExtension(path).Equals(Convert.ToHexString(SHA256.HashData(identity)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException();
        return snapshot;
    }

    private static byte[] Identity(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        return Encoding.UTF8.GetBytes("{\"State\":" + (document.RootElement.TryGetProperty("State", out var state) ? state.GetRawText() : "null") +
            ",\"Draft\":" + (document.RootElement.TryGetProperty("Draft", out var draft) ? draft.GetRawText() : "null") + "}");
    }

    private static void Validate(CalculationHistorySnapshot snapshot)
    {
        if (snapshot.Schema != 1 || snapshot.OrderId == Guid.Empty || snapshot.ProjectId == Guid.Empty ||
            snapshot.OrderVersion < 0 || snapshot.CapturedBy == Guid.Empty || snapshot.CapturedAt == default || snapshot.State is null && snapshot.Draft is null ||
            snapshot.State is { Parameters: null } or { Calculation: null } || snapshot.Draft is { Values: null })
            throw new WorkflowValidationException("Снимок предыдущего расчёта повреждён.");
        snapshot.State?.Report?.Validate(); snapshot.Draft?.Report?.Validate();
    }
}
