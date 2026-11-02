using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

namespace Jornal.Workflow.Desktop;

/// <summary>One row of badges; overflow remains available in the +N tooltip.</summary>
public sealed class WorkflowBadgeStrip : Panel
{
    public static readonly StyledProperty<IReadOnlyList<WorkflowBadgeItem>?> ItemsProperty =
        AvaloniaProperty.Register<WorkflowBadgeStrip, IReadOnlyList<WorkflowBadgeItem>?>(nameof(Items));
    private const double Gap = 4;
    private readonly List<Border> _badges = [];
    private readonly WorkflowBadge _more = new() { IsVisible = false, Height = 18, Padding = new Thickness(5, 0) };
    private int _visibleCount = -1;
    public IReadOnlyList<WorkflowBadgeItem>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public WorkflowBadgeStrip() { ClipToBounds = true; Children.Add(_more); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            Children.Clear(); _badges.Clear(); _visibleCount = -1;
            foreach (var item in Items ?? [])
            {
                var badge = new WorkflowBadge { Label = item.Label, Icon = item.IconPath is { } path ? Geometry.Parse(path) : null,
                    Height = 18, Padding = new Thickness(5, 0) };
                AutomationProperties.SetName(badge, item.Hint);
                ToolTip.SetTip(badge, Hint(item.Hint));
                var container = new Border { Child = badge };
                _badges.Add(container); Children.Add(container);
            }
            Children.Add(_more); InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_badges.Count == 0) { _more.IsVisible = false; return default; }
        foreach (var container in _badges) container.Child!.Measure(Size.Infinity);
        var count = _badges.Count;
        var total = _badges.Sum(b => b.Child!.DesiredSize.Width) + (count - 1) * Gap;
        if (total > availableSize.Width)
        {
            // Reserve a stable upper bound for +N, without changing its text during measurement.
            var counterWidth = 16 + 8 * (1 + count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
            var used = 0d; count = 0;
            foreach (var badge in _badges)
            {
                var next = badge.Child!.DesiredSize.Width + Gap;
                if (used + next + counterWidth > availableSize.Width) break;
                used += next; count++;
            }
        }
        for (var i = 0; i < _badges.Count; i++)
        {
            _badges[i].IsVisible = i < count;
            if (i < count) _badges[i].Measure(_badges[i].Child!.DesiredSize);
        }
        _more.IsVisible = count < _badges.Count;
        if (_more.IsVisible)
        {
            _more.Label = "+" + (_badges.Count - count);
            if (_visibleCount != count)
            {
                var text = string.Join("\n", Items!.Skip(count).Select(item => item.Hint));
                ToolTip.SetTip(_more, Hint(text)); AutomationProperties.SetName(_more, text);
            }
            _more.Measure(Size.Infinity);
        }
        _visibleCount = count;
        var width = _badges.Take(count).Sum(b => b.DesiredSize.Width + Gap);
        width = _more.IsVisible ? width + _more.DesiredSize.Width : Math.Max(0, width - Gap);
        return new Size(Math.Min(availableSize.Width, width), 18);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0d;
        foreach (var badge in _badges.Where(b => b.IsVisible))
        {
            badge.Arrange(new Rect(x, 0, badge.DesiredSize.Width, finalSize.Height));
            x += badge.DesiredSize.Width + Gap;
        }
        if (_more.IsVisible) _more.Arrange(new Rect(x, 0, Math.Min(_more.DesiredSize.Width, Math.Max(0, finalSize.Width - x)), finalSize.Height));
        return finalSize;
    }

    private static TextBlock Hint(string text) => new() { Text = text, MaxWidth = 340, TextWrapping = TextWrapping.Wrap };
}
