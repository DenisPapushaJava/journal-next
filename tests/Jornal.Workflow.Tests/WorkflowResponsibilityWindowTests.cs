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

public sealed class WorkflowResponsibilityWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1366)]
    public async Task MenuOpensSeparatePickerAndCancelDoesNotChangeOrder(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); prefs.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var header = window.FindControl<Expander>("OrderHeaderExpander")!; header.IsExpanded = true; Pump();
            var actions = window.FindControl<Button>("WorkflowOrderActions")!; InWindow(actions, window);
            var before = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single();
            OpenPicker();
            var dialog = Assert.IsType<WorkflowResponsibleWindow>(Assert.Single(window.OwnedWindows));
            var input = dialog.FindControl<ComboBox>("ResponsibleEmployeeInput")!;
            Assert.True(input.IsFocused); Assert.Equal(f.Model.Technologist, input.SelectedItem);
            var save = dialog.FindControl<Button>("SaveResponsibleChange")!; Assert.False(save.IsEnabled);
            input.SelectedItem = f.Model.Employees.Last(); Pump(); Assert.True(save.IsEnabled); InWindow(save, dialog);
            Save(dialog, "picker");
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Assert.Empty(window.OwnedWindows); Assert.True(actions.IsFocused);
            Assert.Equal(before.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single().Version);
            f.Model.Blcb = "Оставить черновиком"; f.Model.ResultText = "Мой текст";
            OpenPicker(); dialog = Assert.IsType<WorkflowResponsibleWindow>(Assert.Single(window.OwnedWindows));
            input = dialog.FindControl<ComboBox>("ResponsibleEmployeeInput")!;
            input.SelectedItem = f.Model.Employees.Last(); Pump();
            Click(dialog.FindControl<Button>("SaveResponsibleChange")!);
            await Until(() => window.OwnedWindows.Count == 0 && !f.Model.IsBusy);
            Assert.Null(f.Model.Error); Assert.Equal(f.Model.Employees.Last().Id, f.Model.Technologist!.Id);
            Assert.Equal("Оставить черновиком", f.Model.Blcb); Assert.Equal("Мой текст", f.Model.ResultText);
            Assert.True(actions.IsFocused); Assert.DoesNotContain("Технолог:", f.Model.HeaderSummary);
            Save(window, "card"); Assert.Empty(log.Errors);

            void OpenPicker()
            {
                Click(actions); var menu = Assert.IsType<MenuFlyout>(actions.Flyout); Assert.True(menu.IsOpen);
                Click(menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Сменить ответственного…"))); Pump();
            }
        }
        finally { window.Close(); }

        void Save(TopLevel root, string state)
        {
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
            Directory.CreateDirectory(output);
            root.CaptureRenderedFrame()!.Save(Path.Combine(output, $"responsible-{state}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
        Pump(); Assert.True(done());
    }
    private static void InWindow(Control control, Window window)
    {
        Assert.True(control.IsEffectivelyVisible); var point = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(point.X, 0, window.ClientSize.Width - control.Bounds.Width);
        Assert.InRange(point.Y, 0, window.ClientSize.Height - control.Bounds.Height);
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
