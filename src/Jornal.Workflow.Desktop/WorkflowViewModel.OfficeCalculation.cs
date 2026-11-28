using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private (WorkflowOfficeStorageClient Client, WorkflowOfficeCalculationDraft Draft)? _officeCalculationDraftConflict;
    private bool _syncOfficeCalculations = true;

    internal string? OfficeCalculationConflictFolder => _officeCalculationDraftConflict is { } conflict &&
        ReferenceEquals(conflict.Client, _connection.RemoteClient) && conflict.Draft.OrderId == CurrentOrder?.Id
        ? conflict.Client.ArchivedCalculationDraftFolder(conflict.Draft) : null;

    private async Task<WorkflowWorkspace?> SaveOfficeCalculationAsync(WorkflowOfficeStorageClient remote, WorkflowMdbStore store,
        WorkflowOrder order, Guid actor, WorkflowOfficeCalculationInput[] inputs, CancellationToken token, bool offline = false)
    {
        offline = offline || ShowOfflineJournal;
        using var lease = remote.LockCalculationDraft(order.Id);
        var draft = await Task.Run(() => remote.PreserveCalculationDraftAsync(order, actor, inputs, token, queuedForOffice: offline), token);
        if (offline)
        {
            await QueueOfflineCalculationAsync(remote, draft, token);
            return null;
        }
        try
        {
            var latest = await remote.ReadActiveOrderForWorkingFilesAsync(order.Id, token);
            if (latest.Version != draft.OrderVersion)
                throw new WorkflowValidationException("Заявка изменилась в офисе. Нажмите «Рассчитать», чтобы сверить сохранённый черновик.");
            return await CommitOfficeCalculationAsync(remote, store, latest, draft, inputs, token);
        }
        catch (Exception ex) when (OfficeCalculationInterrupted(ex))
        {
            await QueueOfflineCalculationAsync(remote, draft, token);
            if (ex is not WorkflowOutcomeUnknownException) await ShowOfficeOutageAsync(remote, token);
            return null;
        }
        catch (IOException)
        { throw new WorkflowValidationException(await QueueCalculationExchangeAsync(remote, draft, token)); }
    }

    private static bool OfficeCalculationInterrupted(Exception ex) =>
        ex is WorkflowOfficeSyncUnavailableException or WorkflowOutcomeUnknownException or
        WorkflowValidationException { InnerException: WorkflowOfficeConnectException };

    private async Task QueueOfflineCalculationAsync(WorkflowOfficeStorageClient remote, WorkflowOfficeCalculationDraft draft, CancellationToken token)
    {
        if (!draft.QueuedForOffice)
        {
            draft = draft with { QueuedForOffice = true };
            await Task.Run(() => remote.SaveCalculationDraft(draft));
        }
        _syncOfficeCalculations = true;
        OfflineJournal?.ShowPendingCalculation(draft);
        var message = await QueueCalculationExchangeAsync(remote, draft, token);
        CalculationExchangeStatus = message;
        SetCalculationNotice($"К отправке · {SpecificationDraft.Money(draft.PendingOfficeTotal())} ₽ по готовым проектам. {message}");
    }

    // The draft lease is already held by the save or recovery operation.
    private async Task<string> QueueCalculationExchangeAsync(WorkflowOfficeStorageClient remote,
        WorkflowOfficeCalculationDraft draft, CancellationToken token)
    {
        if (CalculationExchangeRoot is { } folder)
        {
            try
            {
                var status = await Task.Run(() => WorkflowCalculationExport.ExportDraftAsync(remote, draft, folder, token), token);
                CalculationExchangeStatus = status ?? CalculationExchangePending;
                return CalculationExchangeStatus;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
            {
                return draft.QueuedForOffice ? "Расчёт сохранён на этом компьютере. Яндекс Диск недоступен. Отправим расчёт, когда появится связь с офисом." :
                    "Черновик сохранён на этом компьютере. Не удалось выгрузить его на Яндекс Диск. Проверьте папку обмена и повторите «Рассчитать».";
            }
        }
        return draft.QueuedForOffice ? "Расчёт сохранён на этом компьютере. Отправим его, когда появится связь с офисом." :
            "Расчёт сохранён на этом компьютере. Нажмите «Рассчитать», чтобы проверить запись в офисе.";
    }

    private async Task<WorkflowWorkspace> CommitOfficeCalculationAsync(WorkflowOfficeStorageClient remote, WorkflowMdbStore store,
        WorkflowOrder order, WorkflowOfficeCalculationDraft pending, WorkflowOfficeCalculationInput[] inputs, CancellationToken token,
        bool interactive = true)
    {
        var accepted = new List<(WorkflowOfficeCalculationInput Input, WorkflowOfficePreparedCalculation Prepared)>(inputs.Length);
        foreach (var input in inputs)
        {
            var prepared = interactive
                ? await PrepareRemoteCalculationAsync(remote, order, input.ProjectId, pending.Actor, input.Snapshot,
                    input.Baseline, input.Parameters.HasBending, token)
                : await remote.PrepareCalculationAsync(order, input.ProjectId, pending.Actor, input.Snapshot,
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
            if (ShowOfflineJournal)
            {
                await QueueOfflineCalculationAsync(remote, pending, token);
                return;
            }
            _officeCalculationDraftConflict = null;
            try
            {
                if (store.RequiresRefresh) await store.ReadAsync(token);
                var latest = await store.ReadOrderAsync(orderId, token);
                if (HasNewerCalculationEdits(pending))
                    throw new WorkflowOfficeCalculationDraftConflictException("Параметры расчёта изменены после предыдущей отправки.");
                latest = await ConfirmOfficeCalculationAsync(remote, store, latest, pending, ActorId(), OfficeCopiesRoot, token, interactive: true);
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
            catch (Exception ex) when (OfficeCalculationInterrupted(ex))
            {
                await QueueOfflineCalculationAsync(remote, pending, token);
                if (ex is not WorkflowOutcomeUnknownException) await ShowOfficeOutageAsync(remote, token);
            }
            catch (IOException)
            {
                Error = await QueueCalculationExchangeAsync(remote, pending, token);
                SetCalculationNotice(Error, CalculationTarget.Recalculate);
            }
        });
        return resumed;
    }

    private async Task<WorkflowWorkspace> ConfirmOfficeCalculationAsync(WorkflowOfficeStorageClient remote, WorkflowMdbStore store,
        WorkflowWorkspace latest, WorkflowOfficeCalculationDraft pending, Guid actor, string? copiesRoot, CancellationToken token, bool interactive)
    {
        var order = latest.Orders.SingleOrDefault(candidate => candidate.Id == pending.OrderId);
        if (order is not null && pending.IsSavedIn(order))
        {
            await FinishOfficeCalculationDraftAsync(remote, pending);
            return latest;
        }
        if (order is not { IsDeleted: false } || order.Version != pending.OrderVersion || order.HasProjectRename ||
            pending.Actor != actor || pending.Entries.Any(entry => !order.ActiveProjects.Any(project => project.Id == entry.ProjectId)))
            throw new WorkflowOfficeCalculationDraftConflictException("В офисе изменилась заявка или состав расчёта.");
        try
        {
            foreach (var entry in pending.Entries)
                _ = WorkflowChanges.SaveSpecification(latest, order.Id, order.Version, entry.ProjectId, entry.Parameters,
                    entry.Number, entry.Date, pending.Actor, DateTimeOffset.UtcNow, profile: entry.Profile, quotedRate: entry.Rate);
        }
        catch (WorkflowValidationException ex) { throw new WorkflowOfficeCalculationDraftConflictException(ex.Message); }
        var inputs = await remote.ReopenCalculationDraftAsync(pending, order, copiesRoot, token);
        return await CommitOfficeCalculationAsync(remote, store, order, pending, inputs, token, interactive);
    }

    private async Task SynchronizeQueuedCalculationsAsync(WorkflowOfficeStorageClient remote, Guid actor, Func<bool> current, CancellationToken token)
    {
        var copiesRoot = OfficeCopiesRoot;
        _syncOfficeCalculations = false;
        Guid[] queued;
        try { queued = await Task.Run(() => remote.PendingCalculationOrders(token), token); }
        catch
        {
            if (current()) _syncOfficeCalculations = true;
            throw;
        }
        if (!current()) return;
        var confirmed = 0;
        var needsReview = 0;
        foreach (var id in queued)
        {
            if (!current()) return;
            try
            {
                var completed = await Task.Run(async () =>
                {
                    using var lease = remote.LockCalculationDraft(id);
                    if (remote.ReadQueuedCalculation(id, actor) is not { } draft)
                        return false;
                    var store = remote.CreateStore();
                    var latest = await store.ReadOrderAsync(draft.OrderId, token);
                    await ConfirmOfficeCalculationAsync(remote, store, latest, draft, actor, copiesRoot, token, interactive: false);
                    return true;
                }, token);
                if (completed) confirmed++;
            }
            catch (Exception ex) when (OfficeCalculationInterrupted(ex))
            {
                if (current())
                {
                    _syncOfficeCalculations = true;
                    if (ex is not WorkflowOutcomeUnknownException && CanUse && !IsCreating) await ShowOfficeOutageAsync(remote, token);
                    if (current()) await RetryCalculationExchangeAsync(remote, actor, current, token);
                }
                break;
            }
            catch (WorkflowConflictException)
            {
                if (current()) _syncOfficeCalculations = true;
            }
            catch (Exception ex) when (ex is WorkflowOfficeCalculationDraftConflictException or WorkflowOfficeReportConflictException or
                WorkflowValidationException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or OverflowException)
            { needsReview++; }
        }
        if (!current()) return;
        if (needsReview > 0) CalculationExchangeStatus = "Некоторые домашние расчёты требуют сверки. Откройте заявку и нажмите «Рассчитать».";
        else if (confirmed > 0) CalculationExchangeStatus = "Домашние расчёты подтверждены в офисной базе.";
        if (confirmed > 0 && !ShowOfflineJournal && CanUse && !IsCreating && !HasCurrentCardEdits && !HasPendingReportEdits)
            await ReloadCommand.ExecuteAsync(null);
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
