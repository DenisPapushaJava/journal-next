using Avalonia;
using Avalonia.Controls;

namespace Jornal.Desktop.Controls;

/// <summary>Перестраивает постоянные команды в узкой зоне, не скрывая их в меню.</summary>
public sealed class AdaptiveCommandPanel : Panel
{
    public int WideColumns { get; set; } = 2;
    public int CompactColumns { get; set; } = 2;
    private int Columns(double width) => width < 368 ? CompactColumns : WideColumns;
    private double Gap(double width) => width < 368 ? 6 : 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var gap = Gap(availableSize.Width);
        var width = Math.Max(0, (availableSize.Width - (columns - 1) * gap) / columns);
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(width, double.PositiveInfinity));
            height = Math.Max(height, child.DesiredSize.Height);
        }
        var rows = (int)Math.Ceiling((double)Children.Count / columns);
        return new Size(availableSize.Width, rows * height + Math.Max(0, rows - 1) * gap);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var gap = Gap(finalSize.Width);
        var width = Math.Max(0, (finalSize.Width - (columns - 1) * gap) / columns);
        var height = Children.Count == 0 ? 0 : Children.Max(child => child.DesiredSize.Height);
        for (var index = 0; index < Children.Count; index++)
            Children[index].Arrange(new Rect(index % columns * (width + gap), index / columns * (height + gap), width, height));
        return finalSize;
    }
}
