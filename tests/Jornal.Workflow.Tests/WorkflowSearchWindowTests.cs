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

public sealed class WorkflowSearchWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1366)] [InlineData(true, 1366)]
    [InlineData(false, 1180)] [InlineData(true, 1180)]
    public async Task SearchClearEscapeAndShortcutKeepCardAndFitBothThemes(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); prefs.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Pump();
            var input = window.FindControl<TextBox>("WorkflowSearchInput")!;
            var clear = window.FindControl<Button>("ClearWorkflowSearch")!;
            var summary = window.FindControl<TextBlock>("WorkflowSearchSummary")!;
            Assert.False(clear.IsVisible); Assert.NotNull(input.InnerLeftContent);
            var draft = f.Model.SelectedDraft!; draft.Comment = "Несохранённый ввод"; f.Model.ResultText = "Мой текст письма";
            window.KeyPress(Key.F, RawInputModifiers.Control, PhysicalKey.F, "f"); Pump(); Assert.True(input.IsFocused);
            input.Text = "пример ИВАН Ст3"; Pump();
            Assert.Equal(input.Text, f.Model.Search); Assert.Single(f.Model.Orders); Assert.True(clear.IsVisible);
            Assert.Contains("Найдено заявок: 1 · Все даты", summary.Text); Assert.Contains("Материал:", f.Model.Orders[0].SearchDetail);
            InWindow(input, window); InWindow(clear, window); InWindow(summary, window);
            var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame); Save(window, dark, width, "matches");
            Click(clear); Assert.Equal("", input.Text); Assert.Equal("", f.Model.Search); Assert.True(input.IsFocused); Assert.False(clear.IsVisible);
            Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("Несохранённый ввод", draft.Comment); Assert.Equal("Мой текст письма", f.Model.ResultText);
            input.Text = "совпадений точно нет"; Pump(); Assert.Empty(f.Model.Orders);
            Assert.True(window.FindControl<TextBlock>("WorkflowEmptyPeriod")!.IsVisible); Save(window, dark, width, "empty");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Assert.Equal("", input.Text); Assert.True(input.IsFocused); Assert.Single(f.Model.Orders);
            input.Text = project.Name; Pump(); Assert.Single(f.Model.Orders); Assert.Contains("Проект:", f.Model.Orders[0].SearchDetail);
            Save(window, dark, width, "project");
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
        root.CaptureRenderedFrame()!.Save(Path.Combine(output, $"search-{name}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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
