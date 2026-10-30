using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private readonly TimeProvider? _journalClock;
    private readonly Func<string, Action<bool>, Action, Task<IDisposable>>? _journalSubscribe;
    private WorkflowJournalRefresh? _journalRefresh;
    private WorkflowViewModel? _refreshModel;
    private readonly DispatcherTimer _journalTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly HashSet<IPointer> _pressedPointers = [];
    private long _scrollEpoch;
    private (ScrollViewer Viewer, Vector Offset, Guid? Anchor, double Y)? _scrollBeforeRefresh;

    private void InitializeJournalRefresh(WorkflowViewModel model)
    {
        if (ReferenceEquals(_refreshModel, model)) return;
        StopJournalRefresh(); _refreshModel = model;
        _journalRefresh = new((force, token) => model.RefreshAutomaticallyAsync(force, CanApplyJournalRefresh, token),
            action => Dispatcher.UIThread.Post(action), _journalClock, _journalSubscribe);
        model.PropertyChanged += OnRefreshModelChanged;
        model.BackgroundListApplying += CaptureJournalScroll;
        model.BackgroundListApplied += RestoreJournalScroll;
        ConfigureJournalRefresh();
        _journalTimer.Tick += OnJournalRefreshTick; if (_journalClock is null) _journalTimer.Start();
    }
    private void InitializeJournalInputTracking()
    {
        AddHandler(PointerPressedEvent, (_, e) => { _pressedPointers.Add(e.Pointer); ++_scrollEpoch; }, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, (_, e) => _pressedPointers.Remove(e.Pointer), RoutingStrategies.Tunnel, true);
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            var p = e.GetCurrentPoint(this).Properties;
            if (e.Pointer.Type != PointerType.Touch && !p.IsLeftButtonPressed && !p.IsRightButtonPressed && !p.IsMiddleButtonPressed && !p.IsXButton1Pressed && !p.IsXButton2Pressed)
                _pressedPointers.Remove(e.Pointer);
        }, RoutingStrategies.Tunnel, true);
        AddHandler(PointerWheelChangedEvent, (_, _) => ++_scrollEpoch, RoutingStrategies.Tunnel, true);
        AddHandler(KeyDownEvent, (_, _) => ++_scrollEpoch, RoutingStrategies.Tunnel, true);
        Deactivated += (_, _) => _pressedPointers.Clear();
        DataContextChanged += (_, _) => { if (!IsVisible) return; if (Model is { } model) InitializeJournalRefresh(model); else StopJournalRefresh(); };
    }
    private bool CanApplyJournalRefresh()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || _pressedPointers.Count != 0 || OwnedWindows.Count != 0) return false;
        // This walk runs only for a due read/application, never on every timer tick.
        return !this.GetVisualDescendants().OfType<Control>().Any(c => c.ContextMenu?.IsOpen == true || c.ContextFlyout?.IsOpen == true ||
            c is ComboBox { IsDropDownOpen: true } || c is Button { Flyout.IsOpen: true });
    }
    private async void OnJournalRefreshTick(object? sender, EventArgs e) => await TickJournalRefreshAsync();
    internal Task TickJournalRefreshAsync()
    {
        return _journalRefresh?.TickAsync(IsVisible && WindowState != WindowState.Minimized,
            Model?.CanRefreshAutomatically == true && OwnedWindows.Count == 0 && _pressedPointers.Count == 0) ?? Task.CompletedTask;
    }
    private async void OnRefreshChangedCard(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true, IsCurrentCardStale: true } model || OwnedWindows.Count != 0) return;
        var id = model.SelectedOrder?.Order.Id;
        var dialog = new WorkflowProjectActionWindow("Обновить карточку?", "Несохранённые параметры этой карточки будут отменены.",
            "Будут загружены сохранённые данные заявки. Ручной текст письма останется.", "Обновить карточку")
        { RequestedThemeVariant = ActualThemeVariant };
        if (await dialog.ShowDialog<bool>(this) && IsVisible && ReferenceEquals(Model, model) && model.SelectedOrder?.Order.Id == id)
        { model.DiscardCardChangesCommand.Execute(null); await model.ReloadCommand.ExecuteAsync(null); }
    }
    private void ConfigureJournalRefresh() => _journalRefresh?.Configure(Model?.IsMdb == true ? Model.WorkspacePath : null,
        Model?.AutomaticJournalRefresh == true);
    private void OnRefreshModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkflowViewModel.AutomaticJournalRefresh) or nameof(WorkflowViewModel.WorkspacePath) or nameof(WorkflowViewModel.DatabaseId))
            ConfigureJournalRefresh();
    }
    private void StopJournalRefresh()
    {
        ++_scrollEpoch; _scrollBeforeRefresh = null;
        _journalTimer.Stop(); _journalTimer.Tick -= OnJournalRefreshTick;
        _journalRefresh?.Dispose(); _journalRefresh = null;
        if (_refreshModel is { } model)
        {
            model.CancelBackgroundRefresh();
            model.PropertyChanged -= OnRefreshModelChanged;
            model.BackgroundListApplying -= CaptureJournalScroll;
            model.BackgroundListApplied -= RestoreJournalScroll;
        }
        _refreshModel = null;
    }
    private void CaptureJournalScroll()
    {
        var viewer = WorkflowOrdersGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null) return;
        var anchor = WorkflowOrdersGrid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(r => r.DataContext is WorkflowOrderRow && r.TranslatePoint(default, WorkflowOrdersGrid) is { } pt && pt.Y + r.Bounds.Height > 32)
            .OrderBy(r => r.TranslatePoint(default, WorkflowOrdersGrid)!.Value.Y).FirstOrDefault();
        _scrollBeforeRefresh = (viewer, viewer.Offset, (anchor?.DataContext as WorkflowOrderRow)?.Order.Id,
            anchor?.TranslatePoint(default, WorkflowOrdersGrid)?.Y ?? 0);
    }
    private void RestoreJournalScroll()
    {
        if (_scrollBeforeRefresh is not { } before || Model is not { } model) return;
        _scrollBeforeRefresh = null;
        var epoch = ++_scrollEpoch; var generation = model.BackgroundGeneration;
        bool Current() => IsVisible && epoch == _scrollEpoch && ReferenceEquals(Model, model) && generation == model.BackgroundGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (!Current()) return;
            if (before.Anchor is not { } id || model.Orders.SingleOrDefault(r => r.Order.Id == id) is not { } row)
            { before.Viewer.Offset = before.Offset; return; }
            WorkflowOrdersGrid.ScrollIntoView(row, null);
            Dispatcher.UIThread.Post(() =>
            {
                if (!Current()) return;
                var control = WorkflowOrdersGrid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(r => ReferenceEquals(r.DataContext, row));
                var delta = control?.TranslatePoint(default, WorkflowOrdersGrid)?.Y - before.Y;
                before.Viewer.Offset = delta is { } dy ? new(before.Offset.X, before.Viewer.Offset.Y + dy) : before.Offset;
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }
}
