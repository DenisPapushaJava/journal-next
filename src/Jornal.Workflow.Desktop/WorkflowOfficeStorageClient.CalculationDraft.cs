using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfficeCalculationInput(Guid ProjectId, string Number, DateOnly Date, CuttingParameters Parameters,
    CuttingRate Rate, string? Profile, ReportFileSnapshot? Snapshot, ReportValues? Baseline);

internal sealed partial class WorkflowOfficeStorageClient
{
    private readonly object _pendingCalculationGate = new();
    private HashSet<Guid>? _pendingCalculationOrders;
    private long _nextCalculationOrderScan;
    private string CalculationDraftsRoot => Path.Combine(profileDirectory, "office-calculation-drafts");
    private string CalculationDraftsFolder => Path.Combine(CalculationDraftsRoot, settings.DatabaseId.ToString("N"));
    private string CalculationDraftRoot(Guid order) => Path.Combine(CalculationDraftsFolder, order.ToString("N"));

    internal Guid[] PendingCalculationOrders(CancellationToken token, bool refresh = false)
    {
        lock (_pendingCalculationGate)
        {
            var now = Environment.TickCount64;
            if (refresh || _pendingCalculationOrders is null || now >= _nextCalculationOrderScan)
            {
                _pendingCalculationOrders = ScanPendingCalculationOrders(token).ToHashSet();
                // Another instance can use the same profile.
                _nextCalculationOrderScan = now + 60_000;
            }
            return [.. _pendingCalculationOrders];
        }
    }

    private IEnumerable<Guid> ScanPendingCalculationOrders(CancellationToken token)
    {
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(CalculationDraftsFolder);
        if (!Directory.Exists(CalculationDraftsFolder)) yield break;
        WorkflowServiceFiles.TryHide(CalculationDraftsRoot);
        foreach (var folder in Directory.EnumerateDirectories(CalculationDraftsFolder))
        {
            token.ThrowIfCancellationRequested();
            if (Guid.TryParseExact(Path.GetFileName(folder), "N", out var id) && HasCalculationDraft(id)) yield return id;
        }
    }

    internal WorkflowOfficeCalculationDraft[] ReadQueuedCalculations(CancellationToken token)
    {
        var queued = new List<WorkflowOfficeCalculationDraft>();
        foreach (var id in PendingCalculationOrders(token))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (ReadQueuedCalculation(id) is { } draft) queued.Add(draft);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or WorkflowValidationException or OverflowException)
            { /* The saved draft remains available for manual recovery. */ }
        }
        return queued.ToArray();
    }

    internal WorkflowOfficeCalculationDraft? ReadQueuedCalculation(Guid order, Guid? actor = null)
    {
        if (ReadCalculationDraft(order) is not { QueuedForOffice: true } draft || actor is { } owner && draft.Actor != owner)
            return null;
        _ = draft.PendingOfficeTotal();
        return draft;
    }

    private string CalculationDraftPath(Guid order) => Path.Combine(CalculationDraftRoot(order), "pending.json");
    internal bool HasCalculationDraft(Guid order) => File.Exists(CalculationDraftPath(order));
    internal string ArchivedCalculationDraftFolder(WorkflowOfficeCalculationDraft draft) =>
        Path.Combine(CalculationDraftRoot(draft.OrderId), draft.Id.ToString("N"));

    internal FileStream LockCalculationDraft(Guid order)
    {
        var path = Path.Combine(CalculationDraftRoot(order), ".lock");
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WorkflowServiceFiles.TryHide(CalculationDraftsRoot);
        var lease = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        WorkflowServiceFiles.TryHide(path);
        return lease;
    }

    // Changes use the local draft lock. Cleanup may read the atomically published record without holding it.
    internal WorkflowOfficeCalculationDraft? ReadCalculationDraft(Guid order)
    {
        var path = CalculationDraftPath(order);
        if (!File.Exists(path)) return null;
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
        using var input = File.OpenRead(path);
        var pending = JsonSerializer.Deserialize<WorkflowOfficeCalculationDraft>(input, JsonOptions);
        if (pending is null || pending.Id == Guid.Empty || pending.OrderId != order || pending.OrderVersion <= 0 ||
            pending.Actor == Guid.Empty || pending.Entries is not { Length: > 0 } ||
            pending.Entries.Any(entry => entry is null || entry.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(entry.Number) ||
                entry.Date == default || entry.Parameters is null || entry.Rate is null) ||
            pending.Entries.Select(entry => entry.ProjectId).Distinct().Count() != pending.Entries.Length)
            throw new WorkflowValidationException("Не удалось прочитать сохранённый черновик расчёта.");
        return pending;
    }

    internal async Task<WorkflowOfficeCalculationDraft> PreserveCalculationDraftAsync(WorkflowOrder order, Guid actor,
        WorkflowOfficeCalculationInput[] inputs, CancellationToken token, bool queuedForOffice = false)
    {
        if (HasCalculationDraft(order.Id))
            throw new WorkflowValidationException("Есть незавершённый расчёт. Нажмите «Рассчитать», чтобы сверить его с офисом.");
        var pending = new WorkflowOfficeCalculationDraft(Guid.NewGuid(), order.Id, order.Version, actor, [])
        { OrderNumber = order.DisplayNumber, QueuedForOffice = queuedForOffice };
        var entries = new List<WorkflowOfficeCalculationDraftEntry>(inputs.Length);
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            AcceptedReport? captured = null;
            if (input.Snapshot is { } snapshot && input.Baseline is { } baseline)
            {
                var owner = Path.Combine(ArchivedCalculationDraftFolder(pending), input.ProjectId.ToString("N"));
                captured = await WorkflowReportFiles.ArchiveAsync(snapshot with { SourcePath = Path.Combine(owner, Path.GetFileName(snapshot.SourcePath)) },
                    baseline, token);
            }
            var reportHash = captured?.Sha256 ?? order.Specifications.SingleOrDefault(s => s.Id == input.ProjectId)?.Current.Report?.Sha256;
            entries.Add(new(input.ProjectId, input.Number, input.Date, input.Parameters, input.Rate, input.Profile, reportHash, captured));
        }
        pending = pending with { Entries = entries.ToArray() };
        token.ThrowIfCancellationRequested();
        SaveCalculationDraft(pending);
        return pending;
    }

    internal void SaveCalculationDraft(WorkflowOfficeCalculationDraft draft)
    {
        WriteLocalState(CalculationDraftPath(draft.OrderId), JsonSerializer.SerializeToUtf8Bytes(draft, JsonOptions));
        lock (_pendingCalculationGate) _pendingCalculationOrders?.Add(draft.OrderId);
    }

    internal async Task<WorkflowOfficeCalculationInput[]> ReopenCalculationDraftAsync(WorkflowOfficeCalculationDraft pending,
        WorkflowOrder order, string? copiesRoot, CancellationToken token)
    {
        var inputs = new List<WorkflowOfficeCalculationInput>(pending.Entries.Length);
        foreach (var entry in pending.Entries)
        {
            var project = order.ActiveProjects.SingleOrDefault(p => p.Id == entry.ProjectId)
                ?? throw new WorkflowConflictException();
            ReportFileSnapshot? snapshot = null;
            if (entry.Captured is { } captured)
            {
                var file = DraftReportPath(pending, entry, captured);
                WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(file);
                if (!File.Exists(file))
                    throw new WorkflowOfficeCalculationDraftConflictException("Снимок спецификации черновика недоступен.");
                var archived = await WorkflowReportFiles.ReadAsync(file, token);
                if (archived.Sha256 != captured.Sha256)
                    throw new WorkflowOfficeCalculationDraftConflictException("Снимок спецификации в черновике изменился.");
                var (folder, _, _) = await OpenCalculationFolderAsync(order.Id, project, copiesRoot, token);
                snapshot = archived with { SourcePath = Child(folder, captured.FileName) };
                try { await WorkflowReportFiles.RevalidateAsync(snapshot, token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
                { throw new WorkflowOfficeCalculationDraftConflictException("Рабочая спецификация отличается от сохранённого черновика."); }
            }
            inputs.Add(new(entry.ProjectId, entry.Number, entry.Date, entry.Parameters, entry.Rate, entry.Profile, snapshot, entry.Captured?.Parsed));
        }
        return inputs.ToArray();
    }

    internal string DraftReportPath(WorkflowOfficeCalculationDraft pending, WorkflowOfficeCalculationDraftEntry entry, AcceptedReport captured)
    {
        captured.Validate();
        var reports = Path.Combine(ArchivedCalculationDraftFolder(pending), entry.ProjectId.ToString("N"), ".workflow-reports");
        return Child(Path.Combine(reports, captured.Sha256), captured.FileName);
    }

    internal void FinishCalculationDraft(WorkflowOfficeCalculationDraft pending, bool archive)
    {
        var folder = ArchivedCalculationDraftFolder(pending);
        if (archive)
        {
            WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(folder);
            Directory.CreateDirectory(folder);
            File.Move(CalculationDraftPath(pending.OrderId), Path.Combine(folder, "draft.json"));
            lock (_pendingCalculationGate) _pendingCalculationOrders?.Remove(pending.OrderId);
            return;
        }

        File.Delete(CalculationDraftPath(pending.OrderId));
        lock (_pendingCalculationGate) _pendingCalculationOrders?.Remove(pending.OrderId);
        foreach (var entry in pending.Entries)
        {
            if (entry.Captured is not { } captured) continue;
            // The accepted copy is already in the office archive. Release only files created for this attempt.
            var path = DraftReportPath(pending, entry, captured);
            File.Delete(path);
            for (var parent = Path.GetDirectoryName(path); parent is not null && !PathsEqual(parent, folder); parent = Path.GetDirectoryName(parent))
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
        }
        if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }
}

internal sealed class WorkflowOfficeCalculationDraftConflictException(string message) : Exception(message);
