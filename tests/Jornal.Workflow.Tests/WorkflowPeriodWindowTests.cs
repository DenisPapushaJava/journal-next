using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowPeriodWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1366)] [InlineData(true, 1366)]
    [InlineData(false, 1180)] [InlineData(true, 1180)]
    public async Task PeriodControlsFitAndCalendarSupportsMouseKeyboardCancellationAndBothThemes(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); prefs.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Pump();
            var picker = window.FindControl<WorkflowDatePicker>("WorkflowPeriodPicker")!;
            var button = picker.FindControl<Button>("DateButton")!;
            InWindow(picker, window); InWindow(window.FindControl<RadioButton>("WorkflowAllDates")!, window);
            Assert.True(f.Model.IsTodaySelected); Assert.Equal(DateTime.Today, picker.SelectedDate);
            Click(button); Assert.True(button.Flyout!.IsOpen);
            var flyout = (Flyout)button.Flyout; var panel = (Control)flyout.Content!;
            var popup = TopLevel.GetTopLevel(panel)!;
            var chosen = panel.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is WorkflowCalendarCell { IsSelected: true });
            Assert.True(chosen.IsFocused); Save(window, dark, width, "period-panel"); Save(popup, dark, width, "calendar-days");
            popup.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null); Pump();
            Assert.Equal(DateTime.Today, f.Model.FilterDate); // Moving focus doesn't apply a day.
            popup.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump();
            Assert.False(flyout.IsOpen); Assert.True(button.IsFocused); Assert.Equal(DateTime.Today.AddDays(-1), f.Model.FilterDate);
            Assert.True(f.Model.IsOrderListEmpty); Assert.True(f.Model.IsOrderOpen);
            Click(button); popup = TopLevel.GetTopLevel(panel)!;
            var before = f.Model.FilterDate;
            Click(panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextMonth"));
            popup.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Assert.False(flyout.IsOpen); Assert.Equal(before, f.Model.FilterDate); Assert.True(button.IsFocused);
            Click(button); popup = TopLevel.GetTopLevel(panel)!;
            Click(panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CalendarHeading"));
            Assert.False(picker.Calendar.IsDays); Assert.Equal(12, picker.Calendar.Cells.Count); Save(popup, dark, width, "calendar-months");
            Click(panel.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CalendarHeading"));
            Save(popup, dark, width, "calendar-years");
            var year = panel.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is WorkflowCalendarCell);
            Click(year);
            Click(panel.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is WorkflowCalendarCell));
            var day = panel.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is WorkflowCalendarCell { IsOutside: false });
            var expected = ((WorkflowCalendarCell)day.DataContext!).Date; Click(day);
            Assert.Equal(expected, f.Model.FilterDate); Assert.False(flyout.IsOpen);
            Click(window.FindControl<RadioButton>("WorkflowAllDates")!); Assert.Null(f.Model.FilterDate); Assert.Null(picker.SelectedDate);
            Click(button); Assert.Equal(DateTime.Today, picker.Calendar.FocusDate); Assert.DoesNotContain(picker.Calendar.Cells, c => c.IsSelected);
            flyout.Hide(); Pump(); Click(window.FindControl<RadioButton>("WorkflowToday")!); Assert.True(f.Model.IsTodaySelected);
            Click(window.FindControl<RadioButton>("WorkflowToday")!); Assert.True(f.Model.IsTodaySelected); Assert.Single(f.Model.Orders);
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors.Distinct()));
        }
        finally { window.Close(); }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
        Pump(); Assert.True(done());
    }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void InWindow(Control control, Window window)
    {
        Assert.True(control.IsEffectivelyVisible); var point = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(point.X, 0, window.ClientSize.Width - control.Bounds.Width);
        Assert.InRange(point.Y, 0, window.ClientSize.Height - control.Bounds.Height);
    }
    private static void Save(TopLevel root, bool dark, int width, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output);
        root.CaptureRenderedFrame()!.Save(Path.Combine(output, $"{name}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
    private sealed class BindingLog : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Errors { get; } = [];
        public BindingLog() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string template) => Errors.Add(template);
        public void Log(LogEventLevel level, string area, object? source, string template, params object?[] values) => Errors.Add(template + " " + string.Join("; ", values));
    }
}
