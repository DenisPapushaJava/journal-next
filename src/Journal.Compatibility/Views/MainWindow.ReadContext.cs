using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Desktop.Tables;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class MainWindow
{
    private JournalTableContext? _readTableContext;
    private readonly List<JournalButton> _readAppearanceButtons = [];
    private IDisposable? _readAppearanceDelay;
    private void ConnectReadContext(MainWindowViewModel model)
    {
        model.ReadSnapshotApplying += OnReadSnapshotApplying;
        model.ReadSnapshotApplied += OnReadSnapshotApplied;
        model.ReadRefreshStarting += OnReadRefreshStarting;
        model.ExplicitReadStarted += OnExplicitReadStarted;
        model.ExplicitReadCompleted += OnExplicitReadCompleted;
    }
    private void DisconnectReadContext(MainWindowViewModel model)
    {
        model.ReadSnapshotApplying -= OnReadSnapshotApplying;
        model.ReadSnapshotApplied -= OnReadSnapshotApplied;
        model.ReadRefreshStarting -= OnReadRefreshStarting;
        model.ExplicitReadStarted -= OnExplicitReadStarted;
        model.ExplicitReadCompleted -= OnExplicitReadCompleted;
        _readTableContext = null;
        ClearReadRefreshAppearance();
    }
    private void OnReadRefreshStarting(object? sender, EventArgs e)
    {
        ClearReadRefreshAppearance();
        // Короткое чтение не должно мигать состоянием всех команд. Реальная
        // блокировка записи остаётся немедленной; при долгом запросе видна через 300 мс.
        _readAppearanceButtons.AddRange(this.GetVisualDescendants().OfType<JournalButton>()
            .Where(button => button.IsEffectivelyEnabled || button == ReloadButton && _viewModel?.CanChoosePeriod == true));
        foreach (var button in _readAppearanceButtons) button.Classes.Add("read-refresh-pending");
        _readAppearanceDelay = DispatcherTimer.RunOnce(ClearReadRefreshAppearance, TimeSpan.FromMilliseconds(300));
    }
    private void ClearReadRefreshAppearance()
    {
        _readAppearanceDelay?.Dispose(); _readAppearanceDelay = null;
        foreach (var button in _readAppearanceButtons) button.Classes.Remove("read-refresh-pending");
        _readAppearanceButtons.Clear();
    }
    private void OnReadSnapshotApplying(object? sender, EventArgs e) => _readTableContext = JournalTableContext.Capture(OrdersGrid);
    private void OnReadSnapshotApplied(object? sender, EventArgs e)
    {
        var snapshot = _readTableContext; _readTableContext = null;
        if (snapshot is null) return;
        var selected = _viewModel!.Orders.Where(row => OrdersGrid.SelectedItems.Contains(row)).ToArray();
        snapshot.Restore(OrdersGrid, selected);
    }
}
