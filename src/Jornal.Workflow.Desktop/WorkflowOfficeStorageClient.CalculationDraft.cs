using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfficeCalculationInput(Guid ProjectId, string Number, DateOnly Date, CuttingParameters Parameters,
    CuttingRate Rate, string? Profile, ReportFileSnapshot? Snapshot, ReportValues? Baseline);

internal sealed record WorkflowOfficeCalculationDraft(Guid Id, Guid OrderId, long OrderVersion, Guid Actor,
    WorkflowOfficeCalculationDraftEntry[] Entries)
{
    internal bool IsSavedIn(WorkflowOrder order) => !order.IsDeleted && order.Id == OrderId && Entries.All(entry =>
        order.ActiveProjects.Any(project => project.Id == entry.ProjectId) &&
        order.Specifications.Any(specification => specification.Id == entry.ProjectId && entry.Matches(specification.Current)));
}

internal sealed record WorkflowOfficeCalculationDraftEntry(Guid ProjectId, string Number, DateOnly Date, CuttingParameters Parameters,
    CuttingRate Rate, string? Profile, string? ReportHash, AcceptedReport? Captured)
{
    internal bool Matches(SpecificationState state) => state.DocumentNumber == Number.Trim() && state.DocumentDate == Date &&
        Parameters.SameInputs(state.Parameters) && state.Calculation == CuttingCalculation.Calculate(Parameters, Rate) &&
        state.ImportProfile == Profile && state.Report?.Sha256 == ReportHash &&
        (Captured is null || string.Equals(state.Report?.FileName, Captured.FileName, StringComparison.OrdinalIgnoreCase));
}

internal sealed partial class WorkflowOfficeStorageClient
{
    private string CalculationDraftRoot(Guid order) =>
        Path.Combine(profileDirectory, "office-calculation-drafts", settings.DatabaseId.ToString("N"), order.ToString("N"));

    private string CalculationDraftPath(Guid order) => Path.Combine(CalculationDraftRoot(order), "pending.json");
    internal bool HasCalculationDraft(Guid order) => File.Exists(CalculationDraftPath(order));
    internal string ArchivedCalculationDraftFolder(WorkflowOfficeCalculationDraft draft) =>
        Path.Combine(CalculationDraftRoot(draft.OrderId), draft.Id.ToString("N"));

    internal FileStream LockCalculationDraft(Guid order)
    {
        var path = Path.Combine(CalculationDraftRoot(order), ".lock");
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        WorkflowOfficeCalculationInput[] inputs, CancellationToken token)
    {
        if (HasCalculationDraft(order.Id))
            throw new WorkflowValidationException("Есть незавершённый расчёт. Нажмите «Рассчитать», чтобы сверить его с офисом.");
        var pending = new WorkflowOfficeCalculationDraft(Guid.NewGuid(), order.Id, order.Version, actor, []);
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
        WriteLocalState(CalculationDraftPath(order.Id), JsonSerializer.SerializeToUtf8Bytes(pending, JsonOptions));
        return pending;
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

    private string DraftReportPath(WorkflowOfficeCalculationDraft pending, WorkflowOfficeCalculationDraftEntry entry, AcceptedReport captured)
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
            return;
        }

        File.Delete(CalculationDraftPath(pending.OrderId));
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
