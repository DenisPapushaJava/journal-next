using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Desktop.DataSources;
using Jornal.Infrastructure.Trash;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty] private bool _automaticJournalRefresh = true;
    private int _trashRetentionDays = OrderTrashService.RetentionDays;
    public int TrashRetentionDays
    {
        get => _trashRetentionDays;
        set
        {
            if (!OrderTrashService.IsSupportedRetention(value) || !SetProperty(ref _trashRetentionDays, value)) return;
            OnPropertyChanged(nameof(TrashRetentionIndex));
        }
    }
    public IReadOnlyList<string> TrashRetentionOptions { get; } = ["30 дней", "60 дней", "90 дней"];
    public int TrashRetentionIndex { get => TrashRetentionDays / 30 - 1; set { if (value is >= 0 and <= 2) TrashRetentionDays = (value + 1) * 30; } }
    public bool CanAutomaticallyRefresh => AutomaticJournalRefresh && _ordersLoaded && !RequiresReload &&
        CanUseJournal && !IsCopyingResult && !IsCopyingSelection && !IsDraggingSpecification &&
        !Updates.IsApplying && !_scopeLoadFailed;
    private bool _automaticReadRunning;

    public async Task<JournalRefreshOutcome> RefreshAutomaticallyAsync(CancellationToken cancellationToken = default)
    {
        if (_automaticReadRunning || !CanAutomaticallyRefresh) return JournalRefreshOutcome.Deferred;
        _automaticReadRunning = true;
        try { return await LoadOrdersScopeAsync(force: false, cancellationToken, automatic: true); }
        finally { _automaticReadRunning = false; }
    }
}
