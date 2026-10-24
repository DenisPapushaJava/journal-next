using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Jornal.Desktop.Controls;
using Jornal.Desktop.ViewModels;
using Jornal.Desktop.Views;

namespace Jornal.Desktop.Customization;

/// <summary>Редактируемая копия компоновки. До «Готово» на диск попадает только прежний вариант.</summary>
public sealed partial class MainWindowCustomization
{
    private static readonly string[] ColumnIds = ["date", "specification", "manager", "customer", "cutting",
        "material", "thickness", "sheet-size", "sheet-count", "cut-length", "weight", "price", "cutting-time", "bending", "comment", "delivery", "status"];
    private static readonly Dictionary<string, double> MinimumWidths = new()
        { ["orders"] = 312, ["clipboard"] = 430 };
    private readonly MainWindow _window;
    private readonly IUiPreferencesStore _store;
    private readonly DataGrid _table;
    private readonly ResponsiveTableColumns _responsiveColumns;
    private readonly Grid _groups;
    private readonly Grid _top;
    private readonly Dictionary<string, Border> _panels;
    private readonly ColumnPreference[] _defaultColumns;
    private readonly DispatcherTimer _saveTimer;
    private ScreenLayout _layout = new();
    private ScreenLayout? _beforeEdit;
    private bool _initialized;
    private bool _applying;
    private bool _dirty;
    private LayoutHandle? _drag;
    private string? _dropTarget;
    private double _resizeStart;
    private double _resizeOriginal;
    private MainWindowViewModel? Model => _window.DataContext as MainWindowViewModel;
    private T Find<T>(string name) where T : Control => _window.FindControl<T>(name)!;

    public MainWindowCustomization(MainWindow window, IUiPreferencesStore store)
    {
        _window = window;
        _store = store;
        _table = Find<DataGrid>("OrdersGrid");
        _responsiveColumns = new(_table);
        _groups = Find<Grid>("CommandGroups");
        _top = Find<Grid>("TopArea");
        _panels = new() { ["orders"] = Find<Border>("OrdersCommands"),
            ["clipboard"] = Find<Border>("ClipboardCommands") };
        _defaultColumns = CaptureColumns();
        Find<ResultButtonsEditor>("ResultButtonsCustomization").AppearanceChanged += OnResultButtonsChanged;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => Flush();
        foreach (var column in _table.Columns.Skip(1)) column.PropertyChanged += OnColumnChanged;
        _table.ColumnReordered += (_, _) => Changed();
        _table.ColumnDisplayIndexChanged += (_, _) => Changed();
        foreach (var name in new[] { "OrdersMoveHandle", "ClipboardMoveHandle", "ResultMoveHandle", "ResultResizeHandle" })
        {
            var handle = Find<LayoutHandle>(name);
            handle.MoveStarted += StartDrag;
            handle.MovePoint += MoveDrag;
            handle.MoveFinished += FinishDrag;
            handle.MoveCanceled += (_, _) => CancelDrag();
            handle.KeyDown += OnHandleKeyDown;
        }
        foreach (var name in new[] { "FirstPanelSplitter" })
        {
            var splitter = Find<GridSplitter>(name);
            splitter.DragCompleted += (_, _) => Changed();
            splitter.KeyUp += (_, _) => Changed();
        }
        foreach (var splitter in WorkspaceSplitters)
        {
            splitter.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
            {
                if (!e.GetCurrentPoint(splitter).Properties.IsLeftButtonPressed) return;
                _workspaceResizePointer = e.Pointer; _workspaceResizeSnapshot = _renderedWorkspace;
                splitter.Classes.Remove("keyboard-resize");
                splitter.Focus(NavigationMethod.Pointer);
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            splitter.GotFocus += (_, e) => splitter.Classes.Set("keyboard-resize",
                e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional);
            splitter.DragCompleted += (_, _) =>
            { _workspaceResizePointer = null; _workspaceResizeSnapshot = null; WorkspaceSplitterChanged(); };
            splitter.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) splitter.Classes.Add("keyboard-resize");
                if (e.Key == Key.Escape && CancelWorkspaceResize()) e.Handled = true;
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            splitter.KeyUp += (_, e) =>
            { if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down) WorkspaceSplitterChanged(); };
        }
        Find<Grid>("BodyGrid").SizeChanged += (_, _) => UpdateResultSize();
        Find<Grid>("Workspace").SizeChanged += (_, _) => UpdateResultSize();
    }

    public void Initialize()
    {
        if (_initialized) return;
        var loaded = _store.Load();
        _applying = true;
        if (Model is { } model)
        {
            model.PreferencesError = loaded.Error;
            if (loaded.Preferences is { } profile)
            {
                model.IsDarkTheme = profile.DarkTheme;
                model.RowHighlightStrength = profile.RowHighlightStrength ?? (profile.EnhancedRowContrast ? 100 : Tables.RowHighlightSettings.Default);
                model.ShowTodayOnStartup = profile.ShowTodayOnStartup;
                model.AutomaticJournalRefresh = profile.AutomaticJournalRefresh;
                model.LockOrderDate = profile.LockOrderDate;
                model.TrashRetentionDays = profile.TrashRetentionDays;
                model.ApplyResultStyle(profile.ResultFormatting ?? new());
                model.ResultRowOrder = profile.ResultRowOrder;
                model.ApplyResultPricing(profile.ResultPricing ?? new());
                model.ApplyNewOrderColor(profile.NewOrderColor ?? new());
            }
            model.ApplyEntrySuggestions(loaded.Preferences?.EntrySuggestions ?? EntrySuggestionDefaults.Create());
        }
        var layout = loaded.Preferences?.Layout ?? ScreenLayout.CreateDefault();
        Apply(layout, upgradeColumnSizing: layout.ColumnSizingVersion < ScreenLayout.CurrentColumnSizingVersion);
        _applying = false;
        _initialized = true;
    }

    public void Begin()
    {
        if (Model is not { IsCustomizing: false, CanUseJournal: true } model) return;
        if (!Flush()) return;
        _beforeEdit = Capture();
        model.IsCustomizing = true;
        _window.Classes.Add("customizing");
        Find<LayoutHandle>("OrdersMoveHandle").Focus();
        UpdateResultSize();
    }

    public bool Save()
    {
        if (_beforeEdit is null) return false;
        CancelDrag();
        var draft = Capture();
        if (!Write(draft)) return false;
        _layout = draft;
        _dirty = false;
        End();
        return true;
    }

    public void Cancel()
    {
        if (_beforeEdit is not { } snapshot) return;
        CancelDrag();
        Apply(snapshot);
        End();
    }

    public void Reset()
    {
        if (_beforeEdit is not null) { CancelDrag(); Apply(ScreenLayout.CreateDefault()); }
    }

    private void End()
    {
        Find<ResultButtonsEditor>("ResultButtonsCustomization").ClosePopups();
        _beforeEdit = null;
        if (Model is { } model) model.IsCustomizing = false;
        _window.Classes.Remove("customizing");
        Find<JournalButton>("PersonalizeButton").Focus();
        UpdateResultSize();
    }

    public void AppearanceChanged()
    {
        if (!_initialized || _applying) return;
        // Тема/параметры результата сохраняются и при последующей отмене пробной компоновки.
        if (_beforeEdit is { } original) Write(original);
        else Changed();
    }

    public void Close()
    {
        Cancel();
        Flush();
        _saveTimer.Stop();
        foreach (var column in _table.Columns.Skip(1)) column.PropertyChanged -= OnColumnChanged;
        _responsiveColumns.Dispose();
        Find<ResultButtonsEditor>("ResultButtonsCustomization").AppearanceChanged -= OnResultButtonsChanged;
        Find<ResultButtonsEditor>("ResultButtonsCustomization").ClosePopups();
    }

    public bool Flush()
    {
        _saveTimer.Stop();
        if (!_dirty || !_initialized || _beforeEdit is not null) return true;
        if (Write(Capture())) _dirty = false;
        return !_dirty;
    }

    public void SetRowHeight(double height)
    {
        if (!_initialized || _applying) return;
        _layout = _layout with { RowHeight = TableDensity.NormalizeHeight(height) };
        ApplyTableDensity();
        Changed();
    }

    public double RowHeight => _layout.RowHeight;

    private void ApplyTableDensity()
    {
        var density = TableDensity.FromHeight(_layout.RowHeight);
        // Проверяем метрики реального шрифта, чтобы замена гарнитуры не обрезала строку.
        var sample = new TextBlock { Text = "ЙДgj 0123456789", FontFamily = _table.FontFamily, FontSize = density.FontSize };
        sample.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var minimum = Math.Ceiling(sample.DesiredSize.Height) + 6;
        var height = Math.Max(density.RowHeight, minimum);
        _table.FontSize = density.FontSize;
        _table.RowHeight = height;
        Find<TextBox>("RowHeightInput").Text = _layout.RowHeight.ToString("0", CultureInfo.InvariantCulture);
        Find<JournalButton>("DecreaseRowHeightButton").IsEnabled = _layout.RowHeight > TableDensity.MinimumHeight;
        Find<JournalButton>("IncreaseRowHeightButton").IsEnabled = _layout.RowHeight < TableDensity.MaximumHeight;
    }

    private bool Write(ScreenLayout layout)
    {
        try
        {
            _store.Save(new UiPreferences { DarkTheme = Model?.IsDarkTheme == true, RowHighlightStrength = Model?.RowHighlightStrength ?? Tables.RowHighlightSettings.Default, ShowTodayOnStartup = Model?.ShowTodayOnStartup ?? true,
                AutomaticJournalRefresh = Model?.AutomaticJournalRefresh ?? true, TrashRetentionDays = Model?.TrashRetentionDays ?? 30,
                LockOrderDate = Model?.LockOrderDate ?? true,
                ResultRowOrder = Model?.ResultRowOrder ?? default,
                Layout = layout with { CompactPanelsPresetApplied = true },
                ResultFormatting = Model?.CaptureResultStyle() ?? new(), ResultPricing = Model?.CaptureResultPricing() ?? new(), NewOrderColor = Model?.CaptureNewOrderColor() ?? new(),
                EntrySuggestions = Model?.CaptureEntrySuggestions() ?? new() });
            if (Model is { } model) model.PreferencesError = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppDiagnostics.Capture(DiagnosticOperation.Preferences, exception);
            if (Model is { } model) model.PreferencesError = "Не удалось сохранить настройки экрана. Проверьте доступ к папке профиля. Изменения пока не записаны.";
            return false;
        }
    }

    private void OnColumnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_responsiveColumns.IsAdjusting) return;
        if (e.Property.Name == nameof(DataGridColumn.IsVisible)) Changed();
        if (e.Property.Name == nameof(DataGridColumn.Width) && e.OldValue is DataGridLength oldWidth &&
            e.NewValue is DataGridLength newWidth && (oldWidth.Value != newWidth.Value || oldWidth.UnitType != newWidth.UnitType)) Changed();
    }

    private void Changed()
    {
        if (!_initialized || _applying || _beforeEdit is not null) return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private ColumnPreference[] CaptureColumns() => _table.Columns.Skip(1).Select((column, index) =>
        new ColumnPreference(ColumnIds[index], column.DisplayIndex, column.IsVisible,
            _responsiveColumns.DesiredWidth(column).IsStar, _responsiveColumns.DesiredWidth(column).Value)).ToArray();

    private ScreenLayout Capture() => (_layout with
    {
        Columns = CaptureColumns(),
        Workspace = CaptureWorkspace(),
        PanelWidths = _layout.Workspace is not null ? _layout.PanelWidths : _panels.ToDictionary(pair => pair.Key,
            pair => Math.Max(MinimumWidths[pair.Key], _groups.ColumnDefinitions[Grid.GetColumn(pair.Value)].ActualWidth))
    }).Normalize();

    private void Apply(ScreenLayout layout, bool upgradeColumnSizing = false)
    {
        var wasApplying = _applying;
        _applying = true;
        _layout = layout.Normalize() with { ColumnSizingVersion = ScreenLayout.CurrentColumnSizingVersion };
        ApplyResultButtons();
        ApplyTableDensity();
        // Сначала восстанавливаем исходные определения: новые столбцы получают безопасные значения.
        var columns = _defaultColumns.ToDictionary(column => column.Id);
        foreach (var column in _layout.Columns)
            if (columns.ContainsKey(column.Id)) columns[column.Id] = column;
        foreach (var (id, index) in ColumnIds.Select((id, index) => (id, index)))
        {
            var preference = columns[id];
            var column = _table.Columns[index + 1];
            // Старые Pixel и короткие Star-единицы переводятся в сопоставимые веса один раз.
            var width = upgradeColumnSizing && preference.Star && preference.Width <= 10
                ? Math.Min(5000, preference.Width * _defaultColumns[index].Width) : preference.Width;
            _responsiveColumns.SetWidth(column, new DataGridLength(width,
                upgradeColumnSizing || preference.Star ? DataGridLengthUnitType.Star : DataGridLengthUnitType.Pixel));
            column.IsVisible = preference.Visible;
        }
        var sorted = columns.Values.OrderBy(column => column.Position)
            .ThenBy(column => Array.IndexOf(ColumnIds, column.Id)).ToArray();
        for (var index = 0; index < sorted.Length; index++)
            _table.Columns[Array.IndexOf(ColumnIds, sorted[index].Id) + 1].DisplayIndex = index + 1;
        ApplyWorkspace();
        if (_layout.Workspace is null)
        {
            _groups.ColumnDefinitions.Clear();
            for (var index = 0; index < _layout.PanelOrder.Length; index++)
            {
                var id = _layout.PanelOrder[index];
                if (index > 0) _groups.ColumnDefinitions.Add(new ColumnDefinition(16, GridUnitType.Pixel));
                _groups.ColumnDefinitions.Add(new ColumnDefinition(_layout.PanelWidths[id], GridUnitType.Star) { MinWidth = MinimumWidths[id] });
                Grid.SetColumn(_panels[id], index * 2);
            }
            Grid.SetRow(Find<Grid>("ResultSlot"), _layout.ResultAboveCommands ? 0 : 2);
            Grid.SetRow(_groups, _layout.ResultAboveCommands ? 2 : 0);
        }
        UpdateResultSize();
        _applying = wasApplying;
    }

    private void UpdateResultSize()
    {
        if (_layout.Workspace is not null) { UpdateWorkspaceSize(); return; }
        if (_top.RowDefinitions.Count != 3) return;
        _top.RowDefinitions[1].Height = new GridLength(12);
        var body = Find<Grid>("BodyGrid");
        var filtersHeight = Math.Max(36, Find<Grid>("FiltersPanel").Bounds.Height);
        var error = Find<Border>("LoadErrorPanel");
        var available = body.Bounds.Height - 160 - 24 - filtersHeight - 12 - 36 - 128 - (error.IsVisible ? error.Bounds.Height + error.Margin.Bottom : 0);
        var height = Math.Min(_layout.ResultHeight, Math.Max(_layout.ResultMinimumHeight, available));
        _top.RowDefinitions[_layout.ResultAboveCommands ? 0 : 2].Height = new GridLength(height);
        _top.RowDefinitions[_layout.ResultAboveCommands ? 2 : 0].Height = new GridLength(160);
    }

    private void StartDrag(object? sender, EventArgs e)
    {
        if (sender is not LayoutHandle handle || handle.PanelId != "result-size" && _beforeEdit is null) return;
        _drag = handle;
        _resizeStart = _layout.Workspace is null ? Find<Grid>("ResultSlot").Bounds.Height : Find<Grid>("DockArea").Bounds.Height;
        _resizeOriginal = _layout.Workspace?.Height ?? _layout.ResultHeight;
    }

    private void MoveDrag(object? sender, Point point)
    {
        if (_drag is not { } handle) return;
        if (handle.PanelId == "result-size")
        {
            ResizeHeight(_resizeStart + point.Y - handle.StartPoint.Y);
            UpdateResultSize();
            return;
        }
        if (!Find<Border>("DragPreview").IsVisible && Math.Abs(point.X - handle.StartPoint.X) + Math.Abs(point.Y - handle.StartPoint.Y) < 6) return;
        ShowDragPreview(handle, point);
        Find<TextBlock>("DragPreviewText").Text = PanelTitle(handle.PanelId);
        _dropTarget = null;
        foreach (var panel in _panels.Values) panel.Classes.Remove("drop-target");
        var guide = Find<Border>("DropGuide");
        guide.IsVisible = false;
        Find<Border>("ResultPanel").Classes.Remove("drop-target");
        if (_layout.Workspace is not null) { FindWorkspaceTarget(handle, point); return; }
        if (handle.PanelId == "result")
        {
            var bounds = BoundsInWindow(_groups);
            if (!bounds.Inflate(new Thickness(0, 16)).Contains(point)) return;
            _dropTarget = "result";
            guide.Height = 3;
            Find<TextBlock>("DropGuideText").Text = "";
            guide.Width = _groups.Bounds.Width;
            Canvas.SetLeft(guide, bounds.Left);
            Canvas.SetTop(guide, _layout.ResultAboveCommands ? bounds.Bottom + 4 : bounds.Top - 7);
            guide.IsVisible = true;
        }
        else
        {
            foreach (var (id, panel) in _panels)
                if (id != handle.PanelId && BoundsInWindow(panel).Contains(point))
                { _dropTarget = id; panel.Classes.Add("drop-target"); ShowDropGuide(panel); break; }
        }
    }

    private Rect BoundsInWindow(Control control) => new Rect(control.Bounds.Size)
        .TransformToAABB(control.TransformToVisual(_window)!.Value);

    private void FinishDrag(object? sender, EventArgs e)
    {
        if (_drag is not { } handle) return;
        var target = _dropTarget;
        var workspaceDrop = _workspaceDrop;
        handle.CancelMove();
        ClearDrag();
        if (handle.PanelId == "result-size") Changed();
        else if (_layout.Workspace is not null && workspaceDrop is not null && WorkspaceFits(workspaceDrop.Root)) ApplyWorkspaceMove(workspaceDrop.Root, workspaceDrop.Source);
        else if (target == "result") Apply(Capture() with { ResultAboveCommands = !_layout.ResultAboveCommands });
        else if (target is not null) Swap(handle.PanelId, target);
    }

    private void Swap(string source, string target)
    {
        var order = _layout.PanelOrder.ToArray();
        var first = Array.IndexOf(order, source);
        var second = Array.IndexOf(order, target);
        if (first < 0 || second < 0) return;
        (order[first], order[second]) = (order[second], order[first]);
        Apply(Capture() with { PanelOrder = order });
    }

    public bool CancelDrag()
    {
        if (CancelWorkspaceResize()) return true;
        if (_drag is not { } handle) return false;
        if (handle.PanelId == "result-size")
        { ResizeHeight(_resizeOriginal); UpdateResultSize(); }
        handle.CancelMove();
        ClearDrag();
        return true;
    }

    private void ClearDrag()
    {
        ClearDragPreview();
        _drag = null;
        _dropTarget = null;
        _workspaceDrop = null;
        foreach (var panel in _panels.Values) panel.Classes.Remove("drop-target");
        Find<Border>("DragPreview").IsVisible = false;
        Find<Border>("DropGuide").IsVisible = false;
    }

    private void OnHandleKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not LayoutHandle handle) return;
        if (handle.PanelId == "result-size" && e.Key is Key.Up or Key.Down)
        {
            var height = _layout.Workspace is null ? Find<Grid>("ResultSlot").Bounds.Height : Find<Grid>("DockArea").Bounds.Height;
            ResizeHeight(height + (e.Key == Key.Up ? -8 : 8));
            UpdateResultSize();
            Changed();
            e.Handled = true;
        }
        else if (_beforeEdit is not null && _layout.Workspace is not null && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        { MoveWorkspaceByKey(handle.PanelId, e.Key, e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; }
        else if (_beforeEdit is not null && handle.PanelId == "result" && e.Key is Key.Up or Key.Down)
        { Apply(Capture() with { ResultAboveCommands = e.Key == Key.Up }); e.Handled = true; }
        else if (_beforeEdit is not null && e.Key is Key.Left or Key.Right)
        {
            var index = Array.IndexOf(_layout.PanelOrder, handle.PanelId);
            var target = index + (e.Key == Key.Left ? -1 : 1);
            if (index >= 0 && target >= 0 && target < _layout.PanelOrder.Length) Swap(handle.PanelId, _layout.PanelOrder[target]);
            e.Handled = true;
        }
    }
}
