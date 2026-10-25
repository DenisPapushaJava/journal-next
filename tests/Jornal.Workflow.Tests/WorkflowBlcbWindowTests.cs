using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowBlcbWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1366)] [InlineData(true, 1366)]
    [InlineData(false, 1180)] [InlineData(true, 1180)]
    public async Task HeaderSavesNumberAloneAndFitsBothDesktopThemes(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        prefs.Update(p => p with { DarkTheme = dark, EntrySuggestions = new() { Managers = ["Вахреева", "Менеджер Б"] } });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var header = window.FindControl<Expander>("OrderHeaderExpander")!; header.IsExpanded = true; Pump();
            var input = window.FindControl<TextBox>("WorkflowBlcbInput")!; var save = window.FindControl<Button>("SaveWorkflowHeader")!;
            InWindow(input, window); InWindow(save, window); Assert.InRange(input.Bounds.Width, 240, 320);
            Assert.DoesNotContain(header.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Год");
            Assert.DoesNotContain(header.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Ответственный технолог");
            Assert.Single(header.GetVisualDescendants().OfType<ComboBox>());
            Assert.DoesNotContain("Технолог:", f.Model.HeaderSummary);
            var manager = window.FindControl<EntrySuggestionComboBox>("WorkflowManagerInput")!;
            Assert.Equal("Иванов", manager.Value); Assert.False(f.Model.HasPendingEdits);
            var editor = manager.GetVisualDescendants().OfType<TextBox>().Single(); editor.Focus();
            window.KeyPress(Key.F4, RawInputModifiers.None, PhysicalKey.F4, null); Pump();
            Assert.True(manager.IsDropDownOpen); Assert.Equal(new[] { "Вахреева", "Менеджер Б" }, manager.Items.Cast<string>());
            Assert.DoesNotContain(f.Model.CurrentEmployee!.Name, manager.Items.Cast<string>());
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Assert.Equal("Иванов", f.Model.ManagerName);
            editor.SelectAll(); window.KeyTextInput("Вах"); Pump();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null); Pump();
            Assert.Equal("Вахреева", f.Model.ManagerName);
            input.Focus(); window.KeyTextInput("00017-А/5"); Pump();
            Assert.Equal("00017-А/5", f.Model.Blcb); Assert.True(f.Model.HasPendingEdits);
            await f.Model.SaveHeaderCommand.ExecuteAsync(null); Pump(); Assert.Null(f.Model.Error); Assert.False(f.Model.HasPendingEdits);
            Assert.EndsWith("БЛЦБ: 00017-А/5", f.Model.HeaderSummary); Assert.Equal("00017-А/5", Assert.Single(f.Model.Orders).Blcb);
            Assert.Equal("Вахреева", Assert.Single(f.Model.Orders).Manager);
            f.Model.Search = "0017-А"; Pump(); Assert.Single(f.Model.Orders);
            Save(window, dark, width);
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
    private static void InWindow(Control control, Window window)
    {
        Assert.True(control.IsEffectivelyVisible); var point = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(point.X, 0, window.ClientSize.Width - control.Bounds.Width);
        Assert.InRange(point.Y, 0, window.ClientSize.Height - control.Bounds.Height);
    }
    private static void Save(TopLevel root, bool dark, int width)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output);
        root.CaptureRenderedFrame()!.Save(Path.Combine(output, $"blcb-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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
