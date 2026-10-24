using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Core.Orders;
using Jornal.Infrastructure.Access;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Desktop.DataSources;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private bool _ordersLoaded;
    private DateOnly? _loadedDate;
    private bool _scopeLoadFailed;
    public event EventHandler<long>? ExplicitReadStarted;
    public event EventHandler<JournalReadCompletion>? ExplicitReadCompleted;
    public event EventHandler? ReadRefreshStarting;
    private long _readGeneration;
    private CancellationTokenSource? _scopeReadCancellation;
    public Task OrdersLoadTask { get; private set; } = Task.CompletedTask;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StartupPeriodIndex))]
    private bool _showTodayOnStartup = true;
    public IReadOnlyList<string> StartupPeriods { get; } = ["Сегодня", "Все даты"];
    public int StartupPeriodIndex { get => ShowTodayOnStartup ? 0 : 1; set => ShowTodayOnStartup = value != 1; }
    public bool IsTodaySelected
    {
        get => FilterDate?.Date == DateTime.Today;
        set { if (value) FilterDate = DateTime.Today; }
    }
    public bool IsAllDatesSelected
    {
        get => FilterDate is null;
        set { if (value) FilterDate = null; }
    }
    public string EmptyOrdersText => FilterDate is { } date ? $"Нет заявок за {date:dd.MM.yyyy}" : "Заявки не найдены";
    private DateOnly? RequestedDate => FilterDate is { } date ? DateOnly.FromDateTime(date) : null;
    private bool CoversRequestedPeriod => _ordersLoaded && (_loadedDate is null || _loadedDate == RequestedDate);

    private Task<IReadOnlyList<OrderSummary>> ReadExistingOrdersForDateAsync(DateOnly date, CancellationToken token) =>
        _ordersLoaded && (_loadedDate is null || _loadedDate == date)
            ? Task.FromResult<IReadOnlyList<OrderSummary>>(_allOrders.Where(row => row.OrderDate == date).ToArray())
            : orderReader.ReadDayAsync(date, token);

    // Вызывается после чтения локальных настроек и до первого запроса/открытия окна.
    public void InitializeStartupPeriod()
    {
        if (_ordersLoaded || _scopeReadCancellation is not null) return;
        FilterDate = ShowTodayOnStartup ? DateTime.Today : null;
    }

    public void CancelOrderLoading()
    {
        _readGeneration++;
        var wasReading = _scopeReadCancellation is not null;
        _scopeReadCancellation?.Cancel();
        _scopeReadCancellation = null;
        if (wasReading) { IsLoadingOrders = false; IsRefreshingOrders = false; }
    }

    private async Task<JournalRefreshOutcome> LoadOrdersScopeAsync(bool force, CancellationToken cancellationToken = default,
        bool automatic = false)
    {
        // После неизвестного исхода записи полную сверку подтверждает только явный F5.
        var date = RequiresReload ? null : RequestedDate;
        var preserve = (force || automatic) && _ordersLoaded && CoversRequestedPeriod && !_scopeLoadFailed;
        CancelOrderLoading();
        var generation = _readGeneration;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (automatic) cancellation.CancelAfter(TimeSpan.FromSeconds(30));
        _scopeReadCancellation = cancellation;
        var previousUpdate = UpdatedText;
        var previousNotice = _readSelectionNotice;
        string PreviousStatus() => previousNotice is { } notice && _readSelectionNotice != notice &&
            previousUpdate.EndsWith(notice, StringComparison.Ordinal) ? previousUpdate[..^notice.Length] : previousUpdate;
        var succeeded = false;
        IsLoadingOrders = !preserve;
        if (preserve) ReadRefreshStarting?.Invoke(this, EventArgs.Empty);
        IsRefreshingOrders = preserve;
        if (!automatic) { ErrorMessage = null; ExplicitReadStarted?.Invoke(this, generation); }
        UpdatedText = preserve ? "Обновление журнала…" : date is { } day ? $"Загрузка заявок за {day:dd.MM.yyyy}…" : "Загрузка журнала…";
        if (!preserve)
        {
            UpdateSelection([], null);
            SelectionClearingRequested?.Invoke(this, EventArgs.Empty);
        }
        AppDiagnostics.Current?.Mark(DiagnosticOperation.OrderRead);
        try
        {
            var rows = automatic && orderReader is OrderDurationPreparingReader preparing
                ? await preparing.ReadSnapshotAsync(date, cancellation.Token)
                : date is { } selected ? await orderReader.ReadDayAsync(selected, cancellation.Token)
                : await orderReader.ReadAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _readGeneration || automatic && !AutomaticJournalRefresh) return JournalRefreshOutcome.Deferred;
            var removed = ApplyReadSnapshot(rows, date, preserve);
            _scopeLoadFailed = false;
            if (!automatic)
            {
                ErrorMessage = (orderReader as OrderDurationPreparingReader)?.PreparationWarning;
                if (force) RequiresReload = false;
            }
            SetReadSuccessMessage(removed);
            succeeded = true;
            return JournalRefreshOutcome.Refreshed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || generation != _readGeneration ||
            !automatic && cancellation.IsCancellationRequested)
        {
            if (generation == _readGeneration) UpdatedText = PreviousStatus();
            return JournalRefreshOutcome.Deferred;
        }
        catch (Exception exception)
        {
            if (generation != _readGeneration) return JournalRefreshOutcome.Deferred;
            _scopeLoadFailed = _scopeLoadFailed || !CoversRequestedPeriod;
            UpdatedText = PreviousStatus();
            if (automatic)
            {
                AppDiagnostics.Capture(DiagnosticOperation.OrderRead, exception);
                if (!UpdatedText.StartsWith("Автообновление недоступно", StringComparison.Ordinal))
                    UpdatedText = "Автообновление недоступно · " + UpdatedText;
            }
            else
            {
                ErrorMessage = AppDiagnostics.WithCode(exception is OrderReadException ? exception.Message :
                    "Не удалось загрузить заявки. Попробуйте обновить список (F5).",
                    AppDiagnostics.Capture(DiagnosticOperation.OrderRead, exception));
                AccessHostHelp.ShowFor(exception);
            }
            return JournalRefreshOutcome.Failed;
        }
        finally
        {
            if (generation == _readGeneration)
            {
                _scopeReadCancellation = null;
                IsLoadingOrders = false; IsRefreshingOrders = false;
            }
            if (!automatic) ExplicitReadCompleted?.Invoke(this, new(generation, succeeded));
        }
    }
}

public sealed record JournalReadCompletion(long Id, bool Succeeded);
