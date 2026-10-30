using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private CancellationTokenSource? _backgroundRead;
    private long _backgroundGeneration;
    private IWorkflowWorkspaceStore? _listedSource;
    private long _listedRevision = -1;
    private bool _searchPending;
    [ObservableProperty] private string? _backgroundRefreshError;
    [ObservableProperty] private string? _automaticJournalRefreshError;
    public bool HasAutomaticJournalRefreshError => !string.IsNullOrEmpty(AutomaticJournalRefreshError);
    partial void OnAutomaticJournalRefreshErrorChanged(string? value) => OnPropertyChanged(nameof(HasAutomaticJournalRefreshError));
    public bool HasBackgroundRefreshError => !string.IsNullOrEmpty(BackgroundRefreshError);
    public event Action? BackgroundListApplying;
    public event Action? BackgroundListApplied;
    public bool AutomaticJournalRefresh
    {
        get => _localPreferences.AutomaticJournalRefresh;
        set
        {
            if (value == AutomaticJournalRefresh) return;
            try
            {
                _preferences.Update(p => p with { AutomaticJournalRefresh = value });
                _localPreferences = _localPreferences with { AutomaticJournalRefresh = value };
                CancelBackgroundRefresh(); AutomaticJournalRefreshError = null; BackgroundRefreshError = null; OnPropertyChanged();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { AutomaticJournalRefreshError = "Не удалось сохранить настройку автообновления. Прежнее значение сохранено."; OnPropertyChanged(nameof(AutomaticJournalRefresh)); }
        }
    }
    internal long BackgroundGeneration => _backgroundGeneration;
    public bool CanRefreshAutomatically => AutomaticJournalRefresh && _store is WorkflowMdbStore && _hasLoadedOrders &&
        !_cuttingTaskDetached && CanUse && !_searchPending && !IsCopyingCuttingTask && !RequiresDatabaseRefresh;
    partial void OnBackgroundRefreshErrorChanged(string? value) => OnPropertyChanged(nameof(HasBackgroundRefreshError));
    internal void CancelBackgroundRefresh()
    { ++_backgroundGeneration; _backgroundRead?.Cancel(); }
    private void RecordListedSnapshot(WorkflowWorkspace book)
    {
        // A write/metadata response is deliberately NOT a complete visible-list baseline.
        _listedSource = _store; _listedRevision = book.Version; BackgroundRefreshError = null;
    }

    public async Task<WorkflowRefreshOutcome> RefreshAutomaticallyAsync(bool force, Func<bool>? canApply = null, CancellationToken token = default)
    {
        if (!CanRefreshAutomatically || _backgroundRead is not null || canApply?.Invoke() == false || _store is not WorkflowMdbStore source) return WorkflowRefreshOutcome.Deferred;
        var generation = _backgroundGeneration;
        using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
        _backgroundRead = read;
        bool Current() => generation == _backgroundGeneration && ReferenceEquals(source, _store) && !read.IsCancellationRequested && CanRefreshAutomatically;
        try
        {
            var revision = await source.ReadBackgroundRevisionAsync(read.Token);
            if (!Current()) return WorkflowRefreshOutcome.Deferred;
            if (!force && ReferenceEquals(_listedSource, source) && revision == _listedRevision)
            { BackgroundRefreshError = null; return WorkflowRefreshOutcome.Unchanged; }
            ConfigureDatabaseQuery();
            var prepared = await source.PrepareBackgroundReadAsync(read.Token);
            if (!Current() || canApply?.Invoke() == false) return WorkflowRefreshOutcome.Deferred;
            // Selection and drafts can change during I/O without changing the search.
            // Let the store reject a snapshot which did not retain the now-open card.
            ConfigureDatabaseQuery();
            if (!source.TryAcceptBackgroundRead(prepared)) return WorkflowRefreshOutcome.Deferred;
            ApplyBackgroundSnapshot(prepared.Workspace);
            RecordListedSnapshot(prepared.Workspace);
            await RememberStartupProfileAsync();
            return WorkflowRefreshOutcome.Refreshed;
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested && generation == _backgroundGeneration && !_cuttingTaskDetached && AutomaticJournalRefresh)
            { BackgroundRefreshError = "Автообновление: база не ответила вовремя. Показаны ранее загруженные данные."; return WorkflowRefreshOutcome.Failed; }
            return WorkflowRefreshOutcome.Deferred;
        }
        catch (WorkflowConflictException) { return WorkflowRefreshOutcome.Deferred; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation != _backgroundGeneration || !ReferenceEquals(source, _store) || _cuttingTaskDetached) return WorkflowRefreshOutcome.Deferred;
            BackgroundRefreshError = ex is WorkflowValidationException or InvalidDataException ? ex.Message :
                "Автообновление недоступно. Показаны ранее загруженные данные; проверьте сеть или нажмите F5.";
            return WorkflowRefreshOutcome.Failed;
        }
        finally { if (ReferenceEquals(_backgroundRead, read)) _backgroundRead = null; }
    }

    private void ApplyBackgroundSnapshot(WorkflowWorkspace book)
    {
        // In particular, a changed revision outside the current filter must not rebuild the UI.
        if (JsonSerializer.Serialize(_workspace with { Version = 0 }) == JsonSerializer.Serialize(book with { Version = 0 }))
        { _workspace = book; return; }
        var old = CurrentOrder; var dirty = HasCurrentCardEdits;
        var managerEdited = old is not null && !ManagerName.Trim().Equals(EmployeeName(old.ManagerId), StringComparison.OrdinalIgnoreCase);
        var noSelection = SelectedOrder is null;
        CaptureCard(); var state = _openOrderId is { } id ? _cards.GetValueOrDefault(id) : null;
        BackgroundListApplying?.Invoke();
        try
        {
            _loadedOrderSnapshot = old; _workspace = book;
            RefreshEmployees(_connection.EmployeeId);
            if (!IsCreating && old is not null)
            {
                var current = book.Orders.SingleOrDefault(o => o.Id == old.Id);
                if (current is null || current.IsDeleted)
                {
                    if (!dirty && ResultText.Length == 0) ForgetOpenCard();
                    else MarkResultStale();
                }
                else if (!dirty && current.Version != CardVersion)
                { LoadOrder(current); if (state is not null) RestoreCard(current, state); }
                else if (current.Version == CardVersion && !managerEdited) ManagerName = EmployeeName(current.ManagerId);
            }
            RefreshOrders(preserveRows: true);
            if (noSelection || !IsOrderOpen)
            { _refreshing = true; try { SelectedOrder = null; } finally { _refreshing = false; } }
            NotifyPriceCatalog(preserveDrafts: dirty || IsCurrentOrderMissing || CurrentOrder?.IsDeleted == true); NotifyOrder(); NotifyDatabase();
            if (old is not null && (IsCurrentCardStale || IsCurrentOrderMissing || CurrentOrder?.IsDeleted == true)) MarkResultStale();
        }
        finally { BackgroundListApplied?.Invoke(); }
    }
}
