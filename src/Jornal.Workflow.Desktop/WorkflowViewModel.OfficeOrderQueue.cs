using System.Net;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowQueuedOrderRow(Guid Id, string Customer, string Status, string Details, bool CanReview);

public partial class WorkflowViewModel
{
    private WorkflowOfficeOrderCreation[] _queuedOfficeOrders = [];
    private readonly List<(WorkflowWorkspace Workspace, Guid Id, bool Open)> _confirmedOfficeOrders = [];
    private CancellationTokenSource? _officeOrderSync;
    private DateTimeOffset _nextOfficeOrderSync;
    private Guid? _replacingQueuedOrder;
    private Guid? _lastOfficeCreation;
    public string? OfficeCreationNotice { get; private set; }
    public bool HasOfficeCreationNotice => OfficeCreationNotice is not null;
    public WorkflowQueuedOrderRow[] QueuedOfficeOrderRows { get; private set; } = [];
    public bool HasQueuedOfficeOrders => QueuedOfficeOrderRows.Length > 0;

    private void RefreshOfficeOrderQueue()
    {
        _queuedOfficeOrders = [];
        if (_connection.RemoteClient is { } client && _connection.EmployeeId is { } actor)
            TryRestoreOfficeCreation(() => _queuedOfficeOrders = client.ReadQueuedOrders(actor));
        QueuedOfficeOrderRows = _queuedOfficeOrders.Select(order => new WorkflowQueuedOrderRow(order.Id, order.Customer,
            order.Problem is not null ? "Требуется проверка" : order.RetryMessage is not null ? "Ждёт доступа" :
                order.NotSent ? "Ждёт связи" : "Ожидает подтверждения",
            order.Problem ?? order.RetryMessage ?? $"{order.Manager} · сохранено на этом компьютере",
            order.Problem is not null && !string.IsNullOrWhiteSpace(order.Manager))).ToArray();
        OnPropertyChanged(nameof(QueuedOfficeOrderRows));
        OnPropertyChanged(nameof(HasQueuedOfficeOrders));
    }

    private async Task QueueNewOfficeOrderAsync(WorkflowOfficeStorageClient client, Guid actor)
    {
        var manager = NewManager.Trim(); var customer = NewCustomer.Trim();
        var openedOrder = _openOrderId;
        var replacing = _replacingQueuedOrder;
        WorkflowOfficeOrderCreation? pending = null;
        await RunAsync(async () =>
        {
            pending = await Task.Run(() =>
            {
                using var lease = client.LockCreation(actor);
                var previous = replacing is null ? client.ReadOrderCreation(actor) : null;
                if (previous is { NotSent: false } && (previous.Manager != manager || previous.Customer != customer))
                    throw new WorkflowValidationException("Исход предыдущей отправки ещё не подтверждён. Обновите журнал или откройте форму заново, чтобы восстановить реквизиты.");
                var draft = previous is { NotSent: false } ? previous :
                    new WorkflowOfficeOrderCreation(previous?.Id ?? Guid.NewGuid(), manager, customer,
                        previous?.At ?? DateTimeOffset.Now, NotSent: true);
                if (client.ReadQueuedOrder(actor, draft.Id) is null) client.SaveQueuedOrder(actor, draft);
                if (previous is not null) client.CompleteCreation(actor);
                if (replacing is { } rejected) client.CompleteQueuedOrder(actor, rejected);
                return draft;
            });
            CancelNewOrder();
            RefreshOfficeOrderQueue();
        }, "Не удалось сохранить заявку на этом компьютере. Введённые поля остались в форме.");
        if (pending is not null && !ShowOfflineJournal)
            await SynchronizeOfficeOrdersAsync(DateTimeOffset.UtcNow, open: pending.Id, originalCard: openedOrder);
    }

    internal async Task SynchronizeOfficeOrdersAsync(DateTimeOffset now, CancellationToken token = default, Guid? open = null, Guid? originalCard = null)
    {
        if (!CanUse || _officeOrderSync is not null || _cuttingTaskDetached || token.IsCancellationRequested ||
            _connection.RemoteClient is not { } client ||
            _store is not WorkflowMdbStore journalStore) return;
        var actor = _connection.EmployeeId;
        var mayWrite = client.Settings.CanWrite && actor.HasValue;
        ApplyConfirmedOfficeOrders();
        if (open is null && now < _nextOfficeOrderSync) return;
        var queued = mayWrite ? _queuedOfficeOrders.Where(order => order.Problem is null).ToArray() : [];
        var offline = _offlineOfficeFolders is not null;
        if (queued.Length == 0 && !offline && !(mayWrite && _syncOfficeCalculations)) return;

        _nextOfficeOrderSync = now.AddSeconds(10);
        using var sync = CancellationTokenSource.CreateLinkedTokenSource(token);
        _officeOrderSync = sync;
        bool Current() => !sync.IsCancellationRequested && !_cuttingTaskDetached &&
            ReferenceEquals(_connection.RemoteClient, client) && _connection.EmployeeId == actor;
        try
        {
            var writesForbidden = false;
            if (queued.Length == 0 && offline)
            {
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(sync.Token);
                probe.CancelAfter(TimeSpan.FromSeconds(5));
                try { await client.SendAsync(new(1, WorkflowAccessOperation.ReadRevision, ""), probe.Token); }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    if (Current() && mayWrite) await RetryCalculationExchangeAsync(client, actor!.Value, Current, sync.Token);
                    return;
                }
                catch (WorkflowValidationException) { return; }
                if (Current() && !HasCurrentCardEdits && !IsCreating && CanUse) await ReloadCommand.ExecuteAsync(null);
            }
            foreach (var draft in queued)
            {
                if (!Current()) break;
                try
                {
                    var saved = await Task.Run(() => SendQueuedOfficeOrderAsync(client, journalStore, actor!.Value, draft.Id, sync.Token), sync.Token);
                    if (!Current()) break;
                    if (saved is not null)
                        _confirmedOfficeOrders.Add((saved, draft.Id, draft.Id == open && _openOrderId == originalCard));
                }
                catch (WorkflowOfficeConnectionLostException)
                {
                    if (Current() && CanUse && !IsCreating) await ShowOfficeOutageAsync(client, sync.Token);
                    if (Current()) await RetryCalculationExchangeAsync(client, actor!.Value, Current, sync.Token);
                    break;
                }
                catch (WorkflowValidationException ex) when (IsOfficeAccessDenied(ex))
                {
                    writesForbidden = ex.InnerException is HttpRequestException { StatusCode: HttpStatusCode.Forbidden };
                    break;
                }
                catch (Exception ex) when (ex is WorkflowOutcomeUnknownException or WorkflowConflictException or IOException or UnauthorizedAccessException)
                { break; }
            }
            if (!Current()) return;
            if (queued.Length > 0)
            {
                RefreshOfficeOrderQueue();
                var shouldReload = _offlineOfficeFolders is not null && (_confirmedOfficeOrders.Count > 0 || writesForbidden);
                ApplyConfirmedOfficeOrders();
                if (shouldReload && !HasCurrentCardEdits && !IsCreating && CanUse)
                    await ReloadCommand.ExecuteAsync(null);
            }
            if (Current() && _offlineOfficeFolders is null && mayWrite && !writesForbidden && _syncOfficeCalculations && _hasLoadedOrders &&
                CanUse && !IsCreating && !HasCurrentCardEdits && !HasPendingReportEdits && !_calculatingOrder)
                await SynchronizeQueuedCalculationsAsync(client, actor!.Value, Current, sync.Token);
        }
        catch (OperationCanceledException) when (sync.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppDiagnostics.Capture(DiagnosticOperation.Unexpected, ex);
            if (Current()) Error = "Не удалось проверить отправку заявок. Сохранённые заявки будут проверены повторно.";
        }
        finally { if (ReferenceEquals(_officeOrderSync, sync)) _officeOrderSync = null; }
    }

    private static bool IsOfficeAccessDenied(WorkflowValidationException exception) =>
        exception.InnerException is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };

    private static async Task<WorkflowWorkspace?> SendQueuedOfficeOrderAsync(
        WorkflowOfficeStorageClient client, WorkflowMdbStore journalStore, Guid actor, Guid id, CancellationToken token)
    {
        using var lease = client.LockQueuedOrders(actor);
        if (client.ReadQueuedOrder(actor, id) is not { Problem: null } draft) return null;
        var sending = false;
        var confirmed = false;
        try
        {
            var store = client.CreateStore();
            var saved = await store.CreateOfficeOrderAsync(id, draft.Manager, draft.Customer, actor, draft.At, revision =>
            {
                if (!draft.NotSent && draft.BaseRevision != revision) throw new UnconfirmedOfficeOrderException();
                client.SaveQueuedOrder(actor, draft with { NotSent = false, BaseRevision = revision, RetryMessage = null });
                sending = true;
            }, token);
            if (!saved.Workspace.Orders.Any(order => order.Id == id)) throw new WorkflowOutcomeUnknownException();
            confirmed = true;
            var current = await journalStore.AdoptOfficeCreationAsync(store, id, token);
            try { client.RememberOfflineJournal(current, DateTimeOffset.UtcNow, id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
            { AppDiagnostics.Capture(DiagnosticOperation.Unexpected, ex); }
            client.CompleteQueuedOrder(actor, id);
            return current;
        }
        catch (UnconfirmedOfficeOrderException)
        {
            client.SaveQueuedOrder(actor, draft with { Problem = "Офисная база изменилась после неподтверждённой отправки. Проверьте, была ли заявка создана или удалена." });
        }
        catch (WorkflowConflictException) when (draft.NotSent && sending && !confirmed)
        { client.SaveQueuedOrder(actor, draft); }
        catch (WorkflowValidationException ex) when (ex.InnerException is WorkflowOfficeConnectException)
        {
            if (sending && !confirmed) client.SaveQueuedOrder(actor, draft);
            throw new WorkflowOfficeConnectionLostException(ex.Message, ex);
        }
        catch (WorkflowValidationException ex) when (IsOfficeAccessDenied(ex))
        {
            var pending = confirmed ? client.ReadQueuedOrder(actor, id) ?? draft : draft;
            client.SaveQueuedOrder(actor, pending with { RetryMessage = ex.Message });
            throw;
        }
        catch (WorkflowValidationException ex)
        { client.SaveQueuedOrder(actor, (client.ReadQueuedOrder(actor, id) ?? draft) with { Problem = ex.Message }); }
        return null;
    }

    private void ApplyConfirmedOfficeOrders()
    {
        if (_confirmedOfficeOrders.Count == 0 || !CanUse) return;
        var saved = _confirmedOfficeOrders.ToArray();
        _confirmedOfficeOrders.Clear();
        foreach (var result in saved) _workspace = WorkflowOfficeStorageClient.MergeConfirmedOfficeOrder(_workspace, result.Workspace, result.Id);
        if (OfflineJournal is { } previous && _connection.RemoteClient?.ReadOfflineJournal() is { } snapshot && OfflineOfficeFolders is { } folders)
        {
            var updated = new WorkflowOfflineJournal(snapshot, folders, previous) { Search = previous.Search };
            updated.Selected = updated.Orders.SingleOrDefault(order => order.Id == previous.Selected?.Id);
            OfflineJournal = updated;
        }
        RefreshOrders(preserveRows: true);
        var confirmed = saved.Select(result => _workspace.Orders.SingleOrDefault(order => order.Id == result.Id && !order.IsDeleted))
            .LastOrDefault(order => order is not null);
        if (confirmed is not null) ShowOfficeCreationNotice(confirmed);
        var open = saved.LastOrDefault(result => result.Open).Id;
        if (!ShowOfflineJournal && !IsCreating && !HasCurrentCardEdits &&
            _workspace.Orders.SingleOrDefault(order => order.Id == open && !order.IsDeleted) is { } created)
            ShowCreatedOrder(created, _connection.EmployeeId!.Value);
    }

    private void ResetOfficeOrderQueue()
    {
        _officeOrderSync?.Cancel();
        _confirmedOfficeOrders.Clear();
        _replacingQueuedOrder = null;
        ShowOfficeCreationNotice(null);
        _nextOfficeOrderSync = default;
        _syncOfficeCalculations = true;
        RefreshOfficeOrderQueue();
    }

    private void ShowOfficeCreationNotice(WorkflowOrder? order)
    {
        _lastOfficeCreation = order?.Id;
        OfficeCreationNotice = order is null ? null : $"Создана заявка {order.DisplayNumber}";
        OnPropertyChanged(nameof(OfficeCreationNotice));
        OnPropertyChanged(nameof(HasOfficeCreationNotice));
    }

    internal async Task OpenLastOfficeCreationAsync(CancellationToken token)
    {
        if (!CanUse || _lastOfficeCreation is not { } id) return;
        if (IsCreating || HasCurrentCardEdits) { Error = "Сначала завершите редактирование текущей карточки."; return; }
        await RunAsync(async () =>
        {
            _workspace = await _store.ReadOrderAsync(id, token);
            var order = _workspace.Orders.SingleOrDefault(order => order.Id == id && !order.IsDeleted)
                ?? throw new WorkflowValidationException("Эта заявка больше не активна в офисе.");
            RefreshOrders(preserveRows: true);
            SwitchCard(order);
            ShowOfficeCreationNotice(null);
        });
    }

    internal async Task ReviewQueuedOrderAsync(WorkflowQueuedOrderRow row, CancellationToken token)
    {
        if (!CanUse || IsCreating || HasCurrentCardEdits || !row.CanReview ||
            _connection.RemoteClient is not { } client || _connection.EmployeeId is not { } actor ||
            _queuedOfficeOrders.SingleOrDefault(order => order.Id == row.Id && order.Problem is not null) is not { } pending) return;
        await RunAsync(async () =>
        {
            if (ShowOfflineJournal) throw new WorkflowValidationException("Подключитесь к офису, чтобы проверить эту заявку.");
            var book = await _store.ReadOrderAsync(row.Id, token);
            var existing = book.Orders.SingleOrDefault(order => order.Id == row.Id);
            if (existing is not null)
            {
                client.CompleteQueuedOrder(actor, row.Id);
                _workspace = book;
                RefreshOfficeOrderQueue();
                ShowCreatedOrder(existing, actor);
                return;
            }
            CaptureCard();
            _replacingQueuedOrder = row.Id;
            IsCreating = true;
            NewManager = pending.Manager;
            NewCustomer = pending.Customer;
            Error = "Этой заявки сейчас нет в офисе. Если её нужно восстановить как новую, нажмите «Создать».";
        });
    }
}
