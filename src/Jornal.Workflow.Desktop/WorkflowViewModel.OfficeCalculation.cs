using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private (WorkflowOfficeStorageClient Client, WorkflowOfficeCalculationDraft Draft)? _officeCalculationDraftConflict;

    internal string? OfficeCalculationConflictFolder => _officeCalculationDraftConflict is { } conflict &&
        ReferenceEquals(conflict.Client, _connection.RemoteClient) && conflict.Draft.OrderId == CurrentOrder?.Id
        ? conflict.Client.ArchivedCalculationDraftFolder(conflict.Draft) : null;

    private async Task<WorkflowWorkspace> SaveOfficeCalculationAsync(WorkflowOfficeStorageClient remote, WorkflowMdbStore store,
        WorkflowOrder order, Guid actor, WorkflowOfficeCalculationInput[] inputs, CancellationToken token)
    {
        using var lease = remote.LockCalculationDraft(order.Id);
        var draft = await Task.Run(() => remote.PreserveCalculationDraftAsync(order, actor, inputs, token), token);
        try
        {
            var latest = await remote.ReadActiveOrderForWorkingFilesAsync(order.Id, token);
            if (latest.Version != draft.OrderVersion)
                throw new WorkflowValidationException("Заявка изменилась в офисе. Нажмите «Рассчитать», чтобы сверить сохранённый черновик.");
            return await CommitOfficeCalculationAsync(remote, store, latest, draft, inputs, token);
        }
        catch (WorkflowOfficeSyncUnavailableException ex)
        { throw new WorkflowValidationException(ex.Message + " Черновик расчёта сохранён на этом компьютере."); }
        catch (Exception ex) when (ex is WorkflowOutcomeUnknownException or IOException)
        { throw new WorkflowValidationException("Расчёт сохранён на этом компьютере. Нажмите «Рассчитать», чтобы проверить запись в офисе."); }
    }

    private async Task<WorkflowWorkspace> CommitOfficeCalculationAsync(WorkflowOfficeStorageClient remote, WorkflowMdbStore store,
        WorkflowOrder order, WorkflowOfficeCalculationDraft pending, WorkflowOfficeCalculationInput[] inputs, CancellationToken token)
    {
        var accepted = new List<(WorkflowOfficeCalculationInput Input, WorkflowOfficePreparedCalculation Prepared)>(inputs.Length);
        foreach (var input in inputs)
        {
            var prepared = await PrepareRemoteCalculationAsync(remote, order, input.ProjectId, pending.Actor, input.Snapshot,
                input.Baseline, input.Parameters.HasBending, token);
            accepted.Add((input, prepared));
        }
        WorkflowWorkspace Update(WorkflowWorkspace book)
        {
            foreach (var (input, prepared) in accepted)
                book = WorkflowChanges.SaveSpecification(book, order.Id, book.Orders.Single(order => order.Id == pending.OrderId).Version,
                    input.ProjectId, input.Parameters, input.Number, input.Date, pending.Actor, DateTimeOffset.UtcNow,
                    prepared.SourcePath, input.Profile, input.Rate, prepared.Report);
            return book;
        }
        var reports = inputs.Where(input => input.Snapshot is not null)
            .Select(input => new WorkflowOfficeReportVersion(input.ProjectId, input.Snapshot!.Sha256)).ToArray();
        var saved = reports.Length == 0
            ? await store.WriteOrderAsync(order.Id, pending.OrderVersion, Update, token)
            : await store.WriteOfficeCalculationAsync(order.Id, pending.OrderVersion, Update, reports, token);
        var orderAfterWrite = saved.Orders.SingleOrDefault(candidate => candidate.Id == pending.OrderId);
        if (orderAfterWrite is null || !pending.IsSavedIn(orderAfterWrite))
            throw new WorkflowOutcomeUnknownException();
        await FinishOfficeCalculationDraftAsync(remote, pending);
        return saved;
    }

    private static async Task FinishOfficeCalculationDraftAsync(WorkflowOfficeStorageClient remote, WorkflowOfficeCalculationDraft draft)
    {
        try { await Task.Run(() => remote.FinishCalculationDraft(draft, archive: false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
        {
            // The server already confirmed the calculation. A pending record left on disk is checked again on retry.
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
    }

    private async Task<bool> ResumeOfficeCalculationAsync(WorkflowOfficeStorageClient remote, WorkflowMdbStore store,
        Guid orderId, CancellationToken token)
    {
        var resumed = false;
        await RunAsync(async () =>
        {
            using var lease = remote.LockCalculationDraft(orderId);
            var pending = await Task.Run(() => remote.ReadCalculationDraft(orderId), token);
            if (pending is null) { resumed = true; return; }
            _officeCalculationDraftConflict = null;
            try
            {
                if (store.RequiresRefresh) await store.ReadAsync(token);
                var latest = await store.ReadOrderAsync(orderId, token);
                var order = latest.Orders.SingleOrDefault(candidate => candidate.Id == orderId);
                if (HasNewerCalculationEdits(pending))
                    throw new WorkflowOfficeCalculationDraftConflictException("Параметры расчёта изменены после предыдущей отправки.");
                if (order is not null && pending.IsSavedIn(order))
                    await FinishOfficeCalculationDraftAsync(remote, pending);
                else
                {
                    if (order is not { IsDeleted: false } || order.Version != pending.OrderVersion || order.HasProjectRename ||
                        pending.Actor != ActorId() || pending.Entries.Any(entry => !order.ActiveProjects.Any(project => project.Id == entry.ProjectId)))
                        throw new WorkflowOfficeCalculationDraftConflictException("В офисе изменилась заявка или состав расчёта.");
                    try
                    {
                        foreach (var entry in pending.Entries)
                            _ = WorkflowChanges.SaveSpecification(latest, order.Id, order.Version, entry.ProjectId, entry.Parameters,
                                entry.Number, entry.Date, pending.Actor, DateTimeOffset.UtcNow, profile: entry.Profile, quotedRate: entry.Rate);
                    }
                    catch (WorkflowValidationException ex) { throw new WorkflowOfficeCalculationDraftConflictException(ex.Message); }
                    var inputs = await remote.ReopenCalculationDraftAsync(pending, order, OfficeCopiesRoot, token);
                    latest = await CommitOfficeCalculationAsync(remote, store, order, pending, inputs, token);
                }
                if (!ReferenceEquals(remote, _connection.RemoteClient) || CurrentOrder?.Id != orderId) return;
                _workspace = latest;
                RefreshOrders(preserveRows: true);
                LoadReportOrder(latest.Orders.Single(candidate => candidate.Id == orderId), pending.Entries.Select(entry => entry.ProjectId).ToHashSet());
                if (_reportReview?.OrderId == orderId) _reportReview = null;
                resumed = true;
            }
            catch (WorkflowOfficeCalculationDraftConflictException ex)
            {
                _officeCalculationDraftConflict = (remote, pending);
                Error = ex.Message + " Домашний черновик сохранён.";
                SetCalculationNotice(Error, CalculationTarget.OfficeDraft);
            }
            catch (Exception ex) when (ex is WorkflowOutcomeUnknownException or IOException)
            {
                Error = ex is WorkflowOfficeSyncUnavailableException ? ex.Message + " Черновик сохранён на этом компьютере." :
                    "Не удалось проверить расчёт в офисе. Черновик сохранён на этом компьютере.";
                SetCalculationNotice(Error, CalculationTarget.Recalculate);
            }
        });
        return resumed;
    }

    private bool HasNewerCalculationEdits(WorkflowOfficeCalculationDraft pending)
    {
        var drafts = Drafts.Concat(CurrentReportReview?.Candidates.Where(candidate => !candidate.WasAccepted && candidate.Draft is not null)
            .Select(candidate => candidate.Draft!) ?? []);
        foreach (var draft in drafts.Where(draft => draft.IsDirty))
        {
            if (pending.Entries.SingleOrDefault(entry => entry.ProjectId == draft.Id) is not { } previous) continue;
            try
            {
                if (draft.Number.Trim() != previous.Number.Trim() || draft.DocumentDate() != previous.Date ||
                    !draft.Parameters().SameInputs(previous.Parameters) || draft.Rate() != previous.Rate ||
                    (draft.IncomingReport?.Sha256 ?? draft.Saved?.Report?.Sha256) != previous.ReportHash)
                    return true;
            }
            catch (WorkflowValidationException) { return true; }
        }
        return false;
    }

    internal async Task<bool> ArchiveConflictingOfficeCalculationAsync(CancellationToken token)
    {
        if (OfficeCalculationConflictFolder is null || _officeCalculationDraftConflict is not { } conflict) return false;
        var source = _store;
        var archived = false;
        await RunAsync(async () =>
        {
            using var lease = conflict.Client.LockCalculationDraft(conflict.Draft.OrderId);
            var draft = await Task.Run(() => conflict.Client.ReadCalculationDraft(conflict.Draft.OrderId), token);
            if (draft?.Id != conflict.Draft.Id || !ReferenceEquals(source, _store) || CurrentOrder?.Id != draft.OrderId)
                throw new WorkflowConflictException();
            var latest = await source.ReadOrderAsync(draft.OrderId, token);
            await Task.Run(() => conflict.Client.FinishCalculationDraft(draft, archive: true), token);
            _officeCalculationDraftConflict = null;
            if (_reportReview?.OrderId == draft.OrderId) _reportReview = null;
            _workspace = latest;
            RefreshOrders(preserveRows: true);
            if (latest.Orders.SingleOrDefault(order => order.Id == draft.OrderId) is { IsDeleted: false } active)
            {
                LoadReportOrder(active, draft.Entries.Select(entry => entry.ProjectId).ToHashSet());
                SetCalculationNotice("Черновик сохранён отдельно. Открыта актуальная заявка.", CalculationTarget.Recalculate);
                archived = true;
            }
            else SetCalculationNotice("Черновик сохранён отдельно. Заявка больше не активна в офисе.");
        });
        return archived;
    }
}
