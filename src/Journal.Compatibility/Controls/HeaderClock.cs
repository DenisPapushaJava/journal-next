using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;

namespace Jornal.Desktop.Controls;

public sealed class HeaderClock : TextBlock
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private readonly TimeProvider _timeProvider;
    private readonly DispatcherTimer _timer = new();
    private readonly Run _time = new() { FontSize = 18, FontWeight = FontWeight.Medium };
    private readonly Run _date = new() { FontSize = 13, FontWeight = FontWeight.Normal };

    public HeaderClock() : this(TimeProvider.System) { }

    public HeaderClock(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        Inlines = new() { _time, new Run("   "), _date };
        _timer.Tick += (_, _) => Refresh();
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Refresh()
    {
        var now = _timeProvider.GetLocalNow();
        var weekday = Russian.TextInfo.ToTitleCase(now.ToString("ddd", Russian));
        _time.Text = now.ToString("HH:mm", Russian);
        _date.Text = weekday + " " + now.ToString("d MMMM", Russian);
        AutomationProperties.SetName(this, _time.Text + " " + _date.Text);
        // Следующая граница минуты: без секунд на экране и без постоянного секундного опроса.
        var remaining = TimeSpan.TicksPerMinute - now.TimeOfDay.Ticks % TimeSpan.TicksPerMinute;
        _timer.Interval = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond * 100, remaining));
    }
}
