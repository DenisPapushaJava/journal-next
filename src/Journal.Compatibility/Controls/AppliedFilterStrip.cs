using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Controls;

/// <summary>Одна строка фильтров: не поместившиеся условия доступны в отдельном меню.</summary>
public sealed class AppliedFilterStrip : Panel
{
    public static readonly StyledProperty<IReadOnlyList<AppliedTableFilter>?> FiltersProperty =
        AvaloniaProperty.Register<AppliedFilterStrip, IReadOnlyList<AppliedTableFilter>?>(nameof(Filters));
    private const double Gap = 6;
    private readonly List<Border> _items = [];
    private readonly JournalButton _more = new()
    {
        Name = "MoreFiltersButton", Label = "Ещё ▾", Width = 90,
        Height = 26, MinHeight = 24, Padding = new Thickness(8, 0), FontSize = 12, IsVisible = false
    };
    private readonly StackPanel _menu = new() { Spacing = 6, Width = 300 };
    private readonly Flyout _flyout;
    private IReadOnlyList<AppliedTableFilter> _overflow = [];
    private bool _focusQueued;

    public AppliedFilterStrip()
    {
        ClipToBounds = true;
        _flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new ScrollViewer
            {
                MaxHeight = 240, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _menu
            }
        };
        _more.Flyout = _flyout;
        Children.Add(_more);
    }

    public IReadOnlyList<AppliedTableFilter>? Filters { get => GetValue(FiltersProperty); set => SetValue(FiltersProperty, value); }
    public IReadOnlyList<AppliedTableFilter> OverflowFilters => _overflow;
    public event EventHandler? FocusFallbackRequested;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FiltersProperty)
        {
            var restoreFocus = _items.Any(item => item.IsKeyboardFocusWithin);
            foreach (var item in _items) Children.Remove(item);
            _items.Clear();
            foreach (var filter in Filters ?? [])
            {
                var item = new Border { Child = CreateChip(filter, false) };
                _items.Add(item); Children.Insert(Children.Count - 1, item);
            }
            InvalidateMeasure();
            if (restoreFocus) QueueFocus();
        }
        if (change.Property == IsEnabledProperty)
        {
            _menu.IsEnabled = IsEnabled;
            if (!IsEnabled) _flyout.Hide();
        }
    }

    private Button CreateChip(AppliedTableFilter filter, bool inMenu)
    {
        var label = new TextBlock
        {
            Text = inMenu ? filter.Description : filter.Summary,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = inMenu ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MaxLines = inMenu ? 3 : 1, VerticalAlignment = VerticalAlignment.Center
        };
        var cross = new TextBlock { Text = "×", FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,14") };
        content.Children.Add(label); content.Children.Add(cross); Grid.SetColumn(cross, 2);
        var button = new Button
        {
            DataContext = filter.Source, Content = content, FontSize = 12,
            MinHeight = 26, Padding = new Thickness(8, 0), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MaxWidth = inMenu ? double.PositiveInfinity : 240
        };
        button.Classes.Add("command"); button.Classes.Add("status-filter-chip");
        if (!inMenu) button.Height = 26;
        AutomationProperties.SetName(button, filter.Description);
        AutomationProperties.SetHelpText(button, filter.ClearLabel);
        ToolTip.SetTip(button, new TextBlock { Text = filter.Description, MaxWidth = 360, TextWrapping = TextWrapping.Wrap });
        button.Click += (_, _) =>
        {
            if (!filter.ClearCommand.CanExecute(null)) return;
            filter.ClearCommand.Execute(null);
            QueueFocus();
        };
        return button;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Измеряем сами кнопки, включая находящиеся в скрытых контейнерах.
        // Видимость контейнера не заменяет условие фильтра и исключает его из Tab/доступности.
        foreach (var item in _items) item.Child!.Measure(new Size(240, 26));
        _more.Measure(new Size(90, 26));
        var total = _items.Sum(item => item.Child!.DesiredSize.Width) + Math.Max(0, _items.Count - 1) * Gap;
        var visibleCount = _items.Count;
        if (total > availableSize.Width)
        {
            var used = 0d;
            visibleCount = 0;
            foreach (var item in _items)
            {
                var next = item.Child!.DesiredSize.Width + Gap;
                if (used + next + _more.Width > availableSize.Width) break;
                used += next; visibleCount++;
            }
        }
        for (var i = 0; i < _items.Count; i++)
        {
            _items[i].IsVisible = i < visibleCount;
            if (i < visibleCount) _items[i].Measure(new Size(_items[i].Child!.DesiredSize.Width, 26));
        }
        _more.IsVisible = visibleCount < _items.Count;
        UpdateOverflow((Filters ?? []).Skip(visibleCount).ToArray());
        if (_more.IsVisible) _more.Measure(new Size(90, 26));
        var width = _items.Take(visibleCount).Sum(item => item.DesiredSize.Width + Gap);
        width = _more.IsVisible ? width + _more.Width : Math.Max(0, width - Gap);
        return new Size(Math.Min(availableSize.Width, width), 26);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        foreach (var item in _items.Where(item => item.IsVisible))
        {
            item.Arrange(new Rect(x, 0, item.DesiredSize.Width, finalSize.Height));
            x += item.DesiredSize.Width + Gap;
        }
        if (_more.IsVisible) _more.Arrange(new Rect(x, 0, _more.Width, finalSize.Height));
        return finalSize;
    }

    private void UpdateOverflow(IReadOnlyList<AppliedTableFilter> overflow)
    {
        if (_overflow.SequenceEqual(overflow)) return;
        var restoreFocus = _menu.IsKeyboardFocusWithin || _more.IsKeyboardFocusWithin && overflow.Count == 0;
        _overflow = overflow;
        _more.Label = $"Ещё {overflow.Count} ▾";
        AutomationProperties.SetName(_more, $"Остальные фильтры: {overflow.Count}");
        _menu.Children.Clear();
        foreach (var filter in overflow) _menu.Children.Add(CreateChip(filter, true));
        if (overflow.Count == 0) _flyout.Hide();
        if (restoreFocus) QueueFocus();
    }

    private void QueueFocus()
    {
        if (_focusQueued) return;
        _focusQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _focusQueued = false;
            if (TopLevel.GetTopLevel(this) is null) return;
            if (_flyout.IsOpen && _menu.Children.FirstOrDefault() is { } menuItem) menuItem.Focus();
            else if (_more.IsVisible) _more.Focus();
            else if (_items.FirstOrDefault(item => item.IsVisible)?.Child is { } item) item.Focus();
            else FocusFallbackRequested?.Invoke(this, EventArgs.Empty);
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _flyout.Hide(); base.OnDetachedFromVisualTree(e); }
}
