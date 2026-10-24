using Avalonia.Controls.Primitives;

namespace Jornal.Desktop.Controls;

/// <summary>Восстанавливает значение вместе с фактической прокруткой DataGrid.</summary>
public sealed class JournalScrollBar : ScrollBar
{
    protected override Type StyleKeyOverride => typeof(ScrollBar);

    public void ScrollTo(double offset)
    {
        SetCurrentValue(ValueProperty, Math.Clamp(offset, Minimum, Maximum));
        OnScroll(ScrollEventType.ThumbTrack);
    }
}
