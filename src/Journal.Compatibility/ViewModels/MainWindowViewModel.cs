using Jornal.Infrastructure.Diagnostics;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;
using Jornal.Desktop.DataSources;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel(IOrderReader orderReader, IManualOrderCreator? orderCreator = null,
    IOrderBatchEditor? orderEditor = null, OrderSourceManager? sourceManager = null, AppUpdatesViewModel? appUpdates = null,
    Jornal.Infrastructure.Trash.ITrashStore? trashStore = null) : ObservableObject
{
    private IReadOnlyList<OrderSummary> _allOrders = [];

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSpecificSearchField))] private int _searchScopeIndex;
    [ObservableProperty] private string _countText = "Заявок: 0";
    [ObservableProperty] private string _selectionText = "Выбрано: 0";
    [ObservableProperty] private string _updatedText = "F5 — обновить список";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmptyState))] private bool _hasNoOrders = true;
    [ObservableProperty] private DateTime? _filterDate;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ThemeActionLabel))]
    private bool _isDarkTheme;

    [ObservableProperty] private bool _isCustomizing;
    [ObservableProperty] private bool _isUpdatingOrders;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowEmptyState))] private bool _isLoadingOrders;
    [ObservableProperty] private bool _isRefreshingOrders;

    partial void OnIsCustomizingChanged(bool value) => NotifyJournalAvailability();
    partial void OnIsUpdatingOrdersChanged(bool value) => NotifyJournalAvailability();
    partial void OnIsLoadingOrdersChanged(bool value) => NotifyJournalAvailability();
    partial void OnIsRefreshingOrdersChanged(bool value) => NotifyJournalAvailability();

    private void NotifyJournalAvailability()
    {
        foreach (var name in new[] { nameof(CanUseJournal), nameof(CanCreateOrder), nameof(CanImportSpecification),
            nameof(CanCheckUpdates), nameof(CanConfigureScreen), nameof(CanInteractWithTable), nameof(CanUpdateSelectedOrders),
            nameof(CanAddResult), nameof(CanClearResult), nameof(CanCopyResult), nameof(CanEditResult), nameof(CanConfigureSource),
            nameof(CanEditOrder), nameof(CanTrashOrder), nameof(CanOpenTrash), nameof(MaintenanceHint), nameof(EditOrderHint),
            nameof(TrashOrderHint), nameof(CanClearSelection), nameof(CanOpenPrices), nameof(CanEnterSearch), nameof(CanChoosePeriod), nameof(CanBrowseJournal) })
            OnPropertyChanged(name);
        NotifyClipboardAvailability();
        foreach (var filter in ColumnFilters) filter.NotifyAvailability();
        ReloadCommand.NotifyCanExecuteChanged();
        SetOrderColorCommand.NotifyCanExecuteChanged();
    }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPreferencesError))] private string? _preferencesError;
    public bool HasPreferencesError => !string.IsNullOrEmpty(PreferencesError);
    public bool CanEnterSearch => !IsCustomizing && !IsUpdatingOrders && (!IsLoadingOrders || _scopeReadCancellation is not null) && !IsImportingSpecification && !IsCopyingSelection;
    public bool CanChoosePeriod => CanEnterSearch && !IsSearchPending;
    public bool CanBrowseJournal => CanChoosePeriod && !IsLoadingOrders && !_scopeLoadFailed;
    public bool CanUseJournal => CanBrowseJournal && !IsRefreshingOrders;
    public bool CanConfigureScreen => CanUseJournal;
    public bool CanInteractWithTable => !IsUpdatingOrders && !IsLoadingOrders && !IsImportingSpecification && !IsSearchPending && !_scopeLoadFailed;
    public bool CanClearSelection => _selectedCount > 0 && CanBrowseJournal;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError)), NotifyPropertyChangedFor(nameof(ShowEmptyState))] private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public AccessHostHelpViewModel AccessHostHelp { get; } = new();
    partial void OnErrorMessageChanged(string? value)
    {
        if (_selectionNotice is { } notice && notice.Message != value)
        { _selectionNotice = null; OnPropertyChanged(nameof(HasSelectionNotice)); }
        AccessHostHelp.ShowFor(null);
    }
    public bool ShowEmptyState => HasNoOrders && !IsLoadingOrders && !HasError;
    public string ThemeActionLabel => IsDarkTheme ? "Включить светлую тему" : "Включить тёмную тему";
    public ObservableCollection<OrderRowViewModel> Orders => _visibleRows;
    private NewOrderViewModel? _pendingCreationForm;
    [ObservableProperty] private bool _lockOrderDate = true;
    public bool CanCreateOrder => orderCreator is not null && CanUseJournal && !RequiresReload;
    public NewOrderViewModel? CreateOrderForm(bool forImport = false)
    {
        if (!CanCreateOrder || forImport && _pendingCreationForm is not null) return null;
        if (_pendingCreationForm is { } pending)
        {
            _pendingCreationForm = null;
            pending.ExistingOrders = _allOrders;
            pending.ClearExistingOrderLookup();
            pending.ExistingOrdersForDate = ReadExistingOrdersForDateAsync;
            pending.ResumeAfterReload();
            pending.SetEntrySuggestions(_entrySuggestions);
            pending.OrderDate.IsInputEnabled = !LockOrderDate;
            foreach (var item in pending.BatchItems) item.Form.OrderDate.IsInputEnabled = !LockOrderDate;
            return pending;
        }
        var form = new NewOrderViewModel(orderCreator!, _source?.IsLocalCopy == true,
            isDatabase: _source?.Path is not null && _source.IsLocalCopy == false, initialColor: EffectiveNewOrderColor);
        form.ExistingOrders = _allOrders;
        form.OrderDate.IsInputEnabled = !LockOrderDate;
        form.ExistingOrdersForDate = ReadExistingOrdersForDateAsync;
        form.PriceEditorFactory = CreatePriceEditor;
        form.SetEntrySuggestions(_entrySuggestions);
        form.SaveCommand.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning)) IsUpdatingOrders = form.SaveCommand.IsRunning;
        };
        form.ReloadRequired += (_, _) =>
        {
            _pendingCreationForm = form;
            RequiresReload = true;
            ErrorMessage = form.IsBatchMode
                ? "Добавление пакета не подтверждено. Закройте форму и обновите список (F5). Проверьте заявки перед повтором. «Новая заявка» восстановит весь ввод."
                : "Добавление не подтверждено. Закройте форму и обновите список (F5). Проверьте, появилась ли заявка. Ввод сохранён: «Новая заявка» восстановит его после обновления.";
        };
        return form;
    }

    public void AcceptCreatedOrder(OrderSummary order) => AcceptCreatedOrders([order]);
    public void AcceptCreatedOrders(IReadOnlyList<OrderSummary> orders)
    {
        var order = orders[0];
        _pendingCreationForm = null;
        try
        {
            CancelOrderLoading();
            var ids = orders.Select(row => row.Id).ToHashSet();
            _allOrders = orders.Concat(_allOrders.Where(row => !ids.Contains(row.Id))).ToArray();
            // Новую строку показываем даже при несовпадающих прежних фильтрах.
            var keepDate = RequestedDate is { } selectedDay && orders.All(row => row.OrderDate == selectedDay);
            ResetSearch(resetScope: true, keepDate: keepDate);
            UpdatedText = (orders.Count > 1 ? $"Добавлено заявок: {orders.Count} · " : _source?.Path is not null ? "Заявка добавлена · " : "Демозаявка добавлена · ") + StorageSuffix;
        }
        catch (Exception exception)
        {
            AppDiagnostics.Capture(DiagnosticOperation.OrderRead, exception);
            RequiresReload = true;
            ErrorMessage = orders.Count > 1
                ? $"Пакет из {orders.Count} заявок сохранён, но не удалось обновить экран. Обновите список (F5); повторно добавлять пакет не нужно."
                : $"Заявка сохранена, код {order.Id}, но не удалось обновить экран. Обновите список (F5); повторно добавлять её не нужно.";
        }
    }
    partial void OnFilterDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(IsTodaySelected)); OnPropertyChanged(nameof(IsAllDatesSelected));
        OnPropertyChanged(nameof(EmptyOrdersText));
        OnPropertyChanged(nameof(DefaultSortingSummary));
        ApplySearch();
    }

    public void UpdateSelection(IEnumerable<OrderRowViewModel> selected, OrderRowViewModel? active)
    {
        if (!IsApplyingReadSnapshot) ClearReadSelectionNotice();
        var visibleIds = Orders.Select(row => row.Id).ToHashSet();
        var ids = selected.Select(row => row.Id).Where(visibleIds.Contains).Distinct().ToArray();
        var next = ids.ToHashSet();
        _selectionOrder.RemoveAll(id => !next.Contains(id));
        foreach (var id in ids) if (!_selectedOrderIds.Contains(id)) _selectionOrder.Add(id);
        _selectedOrderIds = next;
        RefreshSelectionNotice();
        var count = _selectedOrderIds.Count;
        _selectedCount = count;
        _activeOrderId = count == 1 ? _selectedOrderIds.Single()
            : active is not null && _selectedOrderIds.Contains(active.Id) ? active.Id : null;
        OnPropertyChanged(nameof(CanAddResult));
        OnPropertyChanged(nameof(CanUpdateSelectedOrders));
        OnPropertyChanged(nameof(CanEditOrder)); OnPropertyChanged(nameof(CanTrashOrder));
        OnPropertyChanged(nameof(EditOrderHint)); OnPropertyChanged(nameof(CanClearSelection));
        OnPropertyChanged(nameof(TrashOrderHint)); NotifyClipboardAvailability();
        SetOrderColorCommand.NotifyCanExecuteChanged();
        SelectionText = $"Выбрано: {count}";
    }

    [RelayCommand] private void SetStatusFilter(OrderStatusFilter filter) => StatusFilter = filter;
    [RelayCommand] private void Today()
    {
        if (IsTodaySelected && _scopeLoadFailed && !IsLoadingOrders) ApplySearch();
        else FilterDate = DateTime.Today;
    }
    [RelayCommand] private void AllDates()
    {
        if (IsAllDatesSelected && _scopeLoadFailed && !IsLoadingOrders) ApplySearch();
        else FilterDate = null;
    }

    [RelayCommand(CanExecute = nameof(CanChoosePeriod))]
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (!CanChoosePeriod) return;
        OrdersLoadTask = LoadOrdersScopeAsync(force: true, cancellationToken);
        await OrdersLoadTask;
    }

}
