using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using Jornal.Core.Orders;
using Jornal.Desktop.DataSources;
using Jornal.Infrastructure.Demo;
using Jornal.Infrastructure.Trash;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private readonly ITrashStore _trashStore = trashStore ?? new MemoryTrashStore();
    private readonly MemoryTrashStore _demoTrash = new();
    private readonly string _demoTrashId = Guid.NewGuid().ToString("N");
    private EditOrderViewModel? _pendingEdit;
    private int _trashCount;
    private int _trashPendingCount;
    public bool HasTrashItems => _trashCount > 0;
    public string TrashBadgeText => _trashCount > 99 ? "99+" : _trashCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string TrashHint => $"Локальная корзина · заявок: {_trashCount}" +
        (_trashPendingCount > 0 ? $". Требуют проверки: {_trashPendingCount}" : "");
    private IOrderMaintenance? Maintenance => _source?.Maintenance ?? orderReader as IOrderMaintenance ?? (orderReader as DemoOrderStore)?.Maintenance;
    private TrashSource TrashSource => new(_source?.Path is null ? "demo" : _source.IsLocalCopy ? "local" : "mdb", _source?.Path ?? _demoTrashId);
    private OrderTrashService TrashService => new(Maintenance!, TrashSource, _source?.Path is null ? _demoTrash : _trashStore,
        retentionDays: TrashRetentionDays);
    public bool CanEditOrder => Maintenance is not null && _selectedCount == 1 && _activeOrderId is not null && CanUseJournal && !RequiresReload;
    public bool CanTrashOrder => Maintenance is not null && _selectedCount > 0 && CanUseJournal && !RequiresReload;
    public bool CanOpenTrash => Maintenance is not null && CanUseJournal && !IsCopyingResult;
    public string? MaintenanceHint => Maintenance is null ? "Изменение MDB доступно в Windows" : null;
    public string? EditOrderHint => MaintenanceHint ?? (_selectedCount == 1 ? null : "Выберите одну заявку");
    public string? TrashOrderHint => MaintenanceHint;

    public async Task<EditOrderViewModel?> CreateEditFormAsync(CancellationToken cancellationToken = default)
    {
        if (!CanEditOrder || Maintenance is not { } backend) return null;
        var original = await ReadActiveRecordAsync(cancellationToken);
        if (original is null) return null;
        if (_pendingEdit is { } pending && pending.Id == original.Id)
        {
            _pendingEdit = null;
            pending.AcceptCurrentSnapshot(original);
            pending.Manager.Suggestions = _entrySuggestions.Managers;
            pending.Customer.Suggestions = _entrySuggestions.Customers;
            pending.Material.Suggestions = _entrySuggestions.Materials;
            pending.OrderDate.IsInputEnabled = !LockOrderDate;
            return pending;
        }
        var form = new EditOrderViewModel(original, backend, SourceDetails, async token =>
        {
            using var registration = token.Register(ReloadCommand.Cancel);
            await ReloadCommand.ExecuteAsync(null).WaitAsync(token);
            if (RequiresReload || HasError) throw new OrderReadException(ErrorMessage ?? "Сначала успешно обновите список (F5).");
            return await backend.ReadRecordAsync(original.Id, token);
        });
        form.BusyChanged += (_, _) => IsUpdatingOrders = form.SaveCommand.IsRunning;
        form.OrderDate.IsInputEnabled = !LockOrderDate;
        form.PriceEditorFactory = CreatePriceEditor;
        form.Manager.Suggestions = _entrySuggestions.Managers;
        form.Customer.Suggestions = _entrySuggestions.Customers;
        form.Material.Suggestions = _entrySuggestions.Materials;
        form.ReloadRequired += (_, _) =>
        {
            _pendingEdit = form; RequiresReload = true;
            ErrorMessage = "Сохранение не подтверждено или обнаружен конфликт. Обновите список (F5); введённые правки сохранены в форме для сверки.";
        };
        return form;
    }

    public async Task<OrderConfirmationViewModel?> CreateTrashFormAsync(CancellationToken cancellationToken = default)
    {
        if (!CanTrashOrder || Maintenance is not { } backend) return null;
        var ids = Orders.Where(row => _selectedOrderIds.Contains(row.Id)).Select(row => row.Id).ToArray();
        var records = new List<OrderRecord>(ids.Length);
        IsLoadingOrders = true; ErrorMessage = null;
        try
        {
            foreach (var id in ids)
            {
                var row = await backend.ReadRecordAsync(id, cancellationToken);
                if (row is null) throw new OrderWriteException(OrderWriteFailure.Conflict,
                    $"Заявка с кодом {id} уже удалена. Группа не удалялась. Обновите список (F5) и проверьте выбор.");
                records.Add(row);
                UpdatedText = $"Подготовка корзины: {records.Count} из {ids.Length}";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) { OnMaintenanceFailure(exception); return null; }
        finally { IsLoadingOrders = false; }
        var service = TrashService;
        OrderConfirmationViewModel form = null!;
        form = new(records.Count == 1 ? "Переместить заявку в корзину?" : $"Переместить в корзину: {records.Count}?",
            string.Join("\n\n", records.Select(Describe)),
            $"Заявки исчезнут из общей базы у всех пользователей. Восстановление на этом компьютере — {service.RetentionPeriodDays} дней.\nИсточник: " + SourceDetails,
            records.Count == 1 ? "В корзину" : $"В корзину ({records.Count})", async () =>
        {
            await WriteMaintenanceAsync(async () =>
            {
                var completed = 0;
                var result = await service.DeleteManyAsync(records, item =>
                {
                    if (item.Outcome == TrashDeleteOutcome.Deleted) completed++;
                    form.Progress = $"Перемещено в корзину: {completed} из {records.Count}";
                });
                var deleted = result.Items.Where(item => item.Outcome == TrashDeleteOutcome.Deleted).Select(item => item.Record.Id).ToHashSet();
                try { if (deleted.Count > 0) AcceptMaintenance(deleted, null); }
                catch (Exception exception) { throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown,
                    "Удаление завершено, но экран не обновлён. Выполните F5; повторять действие не нужно.", exception); }
                await RefreshTrashBadgeAsync(service, TrashSource.Key);
                if (!result.IsComplete)
                {
                    var lines = result.Items.Select(item =>
                    {
                        var status = item.Outcome switch
                        {
                            TrashDeleteOutcome.Deleted => "В корзине",
                            TrashDeleteOutcome.NotAttempted => "Не обрабатывалась",
                            _ => item.Error is OrderWriteException { Failure: OrderWriteFailure.OutcomeUnknown } ? "Нужно проверить" : "Не удалена"
                        };
                        return $"{status} · № {item.Record.Specification ?? "—"} · {item.Record.Date:dd.MM.yyyy} · {item.Record.Customer ?? "—"} (код {item.Record.Id})";
                    });
                    form.ShowOutcome($"В корзине: {deleted.Count} из {records.Count}. Удаление остановлено.\n" +
                        result.Error?.Message + "\n\n" + string.Join("\n", lines) + "\n\nЗакройте окно и обновите список (F5) и корзину перед следующим действием.", result.Error);
                }
            });
        }, OnMaintenanceFailure, destructive: true);
        return form;
    }

    private async Task<OrderRecord?> ReadActiveRecordAsync(CancellationToken cancellationToken)
    {
        if (_activeOrderId is not { } id || Maintenance is not { } backend) return null;
        IsLoadingOrders = true; ErrorMessage = null;
        try
        {
            var row = await backend.ReadRecordAsync(id, cancellationToken);
            if (row is null) throw new OrderWriteException(OrderWriteFailure.Conflict, "Заявка уже удалена. Обновите список (F5).");
            return row;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) { OnMaintenanceFailure(exception); return null; }
        finally { IsLoadingOrders = false; }
    }

    public void AcceptEditedOrder(OrderRecord row)
    {
        _pendingEdit = null;
        try { AcceptMaintenance(row.Id, row); }
        catch (Exception exception)
        {
            RequiresReload = true; ErrorMessage = "Правки сохранены, но экран не обновлён. Выполните F5; повторять запись не нужно.";
            AppDiagnostics.Capture(DiagnosticOperation.Trash, exception);
        }
    }

    private void AcceptMaintenance(int id, OrderRecord? replacement) => AcceptMaintenance(new HashSet<int> { id }, replacement);

    private void AcceptMaintenance(HashSet<int> ids, OrderRecord? replacement)
    {
        CancelOrderLoading();
        var selected = _selectionOrder.ToArray();
        _allOrders = replacement is null ? _allOrders.Where(row => !ids.Contains(row.Id)).ToArray()
            : _allOrders.Where(row => !ids.Contains(row.Id)).Append(replacement.ToSummary()).OrderByDescending(row => row.OrderDate).ThenByDescending(row => row.Id).ToArray();
        ApplySearch();
        var rows = SelectedRowsInOrder(replacement is null ? selected : selected.Append(replacement.Id).Distinct());
        var active = rows.FirstOrDefault(row => ids.Contains(row.Id)) ?? rows.FirstOrDefault();
        UpdateSelection(rows, active);
        SelectionRestoreRequested?.Invoke(this, new(rows.Select(row => row.Id).ToArray(), active?.Id));
        UpdatedText = (replacement is null ? $"В корзину: {ids.Count}" : "Заявка сохранена") + " · " + StorageSuffix;
        if (_resultOrderIds.Overlaps(ids) && ResultText.Length > 0)
        {
            HasResultError = true;
            ResultMessage = "Заявка из результата изменена или удалена. Подготовленный текст сохранён; проверьте его перед отправкой.";
        }
    }

    private void AcceptCommittedMaintenance(int id, OrderRecord? row)
    {
        try { AcceptMaintenance(id, row); }
        catch (Exception exception) { throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown,
            "Запись в базе завершена, но экран не обновлён. Выполните F5; повторять действие не нужно.", exception); }
    }

    public TrashViewModel? CreateTrashViewModel() => CanOpenTrash ? new(TrashService, SourceDetails, AcceptCommittedMaintenance,
        async token =>
        {
            if (RequiresReload || HasError)
            {
                using var registration = token.Register(ReloadCommand.Cancel);
                await ReloadCommand.ExecuteAsync(null).WaitAsync(token);
            }
            if (RequiresReload || HasError) throw new OrderReadException(ErrorMessage ?? "Сначала обновите список (F5).");
        }, async action =>
        {
            var service = TrashService;
            var key = TrashSource.Key;
            try { await WriteMaintenanceAsync(action); }
            finally { await RefreshTrashBadgeAsync(service, key); }
        }, OnMaintenanceFailure, UpdateTrashBadge) : null;

    public async Task CleanupTrashAsync()
    {
        if (Maintenance is null) return;
        await RefreshTrashBadgeAsync(TrashService, TrashSource.Key);
    }

    private async Task RefreshTrashBadgeAsync(OrderTrashService service, string sourceKey)
    {
        try
        {
            var entries = await service.RefreshAsync(reconcile: false);
            if (sourceKey == TrashSource.Key) UpdateTrashBadge(entries);
        }
        catch (Exception exception) { AppDiagnostics.Capture(DiagnosticOperation.Trash, exception); }
    }

    private void UpdateTrashBadge(IReadOnlyList<TrashEntry> entries)
    {
        _trashCount = entries.Count;
        _trashPendingCount = entries.Count(entry => entry.State is TrashState.DeletePending or TrashState.RestorePending);
        OnPropertyChanged(nameof(HasTrashItems)); OnPropertyChanged(nameof(TrashBadgeText)); OnPropertyChanged(nameof(TrashHint));
    }

    private async Task WriteMaintenanceAsync(Func<Task> action)
    {
        if (!CanUseJournal || RequiresReload) throw new OrderWriteException(OrderWriteFailure.NotSaved, "Сначала завершите действие и успешно обновите список (F5).");
        IsUpdatingOrders = true; ErrorMessage = null;
        try { await action(); }
        catch (Exception exception) { OnMaintenanceFailure(exception); throw; }
        finally { IsUpdatingOrders = false; }
    }

    private void OnMaintenanceFailure(Exception exception)
    {
        if (exception is OrderWriteException { RequiresReload: true }) RequiresReload = true;
        ErrorMessage = exception is OrderWriteException or OrderReadException ? exception.Message :
            "Не удалось выполнить действие. Проверьте доступ к базе и локальной корзине. Список и архив сохранены.";
        ErrorMessage = AppDiagnostics.WithCode(ErrorMessage, AppDiagnostics.Capture(DiagnosticOperation.Trash, exception));
        AccessHostHelp.ShowFor(exception);
        AppDiagnostics.Capture(DiagnosticOperation.Trash, exception);
    }

    public static string Describe(OrderRecord row) => $"№ {row.Specification ?? "—"} · {row.Date:dd.MM.yyyy}\nЗаказчик: {row.Customer ?? "—"}\nМенеджер: {row.Manager ?? "—"} · код {row.Id}";
}
