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

public sealed class WorkflowCuttingTaskWindowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task TaskActionFitsWithoutPermanentPreviewAndClipboardHasClearOutcomes(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        for (var i = 0; i < 6; i++) { var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); }
        await f.AcceptAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Update(p => p with { DarkTheme = dark });
        var window = new RecordingWindow(preferences) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Pump();
            var preview = window.FindControl<TextBox>("WorkflowCuttingTaskPreview")!;
            var copy = window.FindControl<Button>("CopyWorkflowCuttingTask")!;
            var copyText = window.FindControl<MenuItem>("CopyWorkflowCuttingTaskText")!;
            var toggle = window.FindControl<Button>("ToggleWorkflowCuttingTaskPreview")!;
            var flyout = Assert.IsType<Flyout>(toggle.Flyout);
            var grid = window.FindControl<DataGrid>("WorkflowProjectsGrid")!;
            var initial = Bounds(window.FindControl<Border>("WorkflowOrderTotal")!, window);
            Assert.False(flyout.IsOpen); InWindow(copy, window);
            Assert.True(initial.Height <= 76, initial.ToString());
            var gridBounds = Bounds(grid, window);
            Assert.True(grid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsEffectivelyVisible && r.Bounds.Height > 0 && Bounds(r, window).Bottom <= gridBounds.Bottom) >= 4);
            Click(copy); await Until(() => f.Model.IsCuttingTaskVisible); Assert.Null(window.Copied);
            Assert.Contains("БЛЦБ", f.Model.CuttingTaskNotice);
            Click(window.FindControl<Button>("GoToWorkflowCuttingTaskProblem")!);
            Assert.True(window.FindControl<Expander>("OrderHeaderExpander")!.IsExpanded); Pump();
            Assert.True(window.FindControl<TextBox>("WorkflowBlcbInput")!.IsKeyboardFocusWithin);
            f.Model.Blcb = "БЛЦБ-009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
            window.FindControl<Expander>("OrderHeaderExpander")!.IsExpanded = false;
            copyText.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); await Until(() => window.Copied is not null);
            Assert.StartsWith("9337  Иванов", window.Copied); Assert.Contains("Задание скопировано", f.Model.CuttingTaskNotice); Assert.False(flyout.IsOpen);
            var calculation = window.FindControl<Border>("WorkflowProjectCalculation")!; var beforePreview = Bounds(calculation, window);
            Click(toggle);
            Assert.True(flyout.IsOpen); Assert.True(preview.IsReadOnly); Assert.Equal(window.Copied, preview.Text);
            Assert.Equal(beforePreview, Bounds(calculation, window));
            foreach (var scroll in TopLevel.GetTopLevel(preview)!.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"Preview overflows: {scroll.Extent} / {scroll.Viewport}");
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is { } output)
            {
                using var popup = TopLevel.GetTopLevel(preview)!.CaptureRenderedFrame();
                popup?.Save(Path.Combine(output, $"task-text-popup-{width}-{dark}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            SaveFrame(window, $"task-preview-{width}-{dark}");
            flyout.Hide(); Pump(); Assert.False(flyout.IsOpen);
            window.FailCopy = true; copyText.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); await Until(() => f.Model.CuttingTaskNotice.Contains("Не удалось", StringComparison.Ordinal));
            Assert.True(copy.IsEffectivelyEnabled); window.FailCopy = false; copyText.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent)); await Until(() => f.Model.CuttingTaskNotice.Contains("Задание скопировано", StringComparison.Ordinal));
            SaveFrame(window, $"task-compact-{width}-{dark}");
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task SavedBendingHasSmallFolderActionAndFailureCanBeRetriedWithoutSave(bool dark)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Update(p => p with { DarkTheme = dark });
        var window = new RecordingWindow(preferences) { DataContext = f.Model, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var open = window.FindControl<Button>("OpenWorkflowBendingFolder")!; Assert.False(open.IsEffectivelyVisible);
            var path = Path.Combine(Path.GetDirectoryName(project.NativePath!)!, "ГИБКА"); await File.WriteAllTextAsync(path, "Не перезаписывать", Token);
            Click(window.FindControl<CheckBox>("WorkflowHasBending")!); Assert.False(open.IsEffectivelyVisible);
            Click(window.FindControl<Button>("SaveWorkflowSpecification")!); await Until(() => !f.Model.IsBusy && f.Model.HasBendingFolderProblem);
            Assert.True(open.IsEffectivelyVisible); InWindow(open, window);
            InWindow(window.FindControl<Button>("RetryWorkflowBendingFolders")!, window);
            SaveFrame(window, "bending-error-" + dark);
            var before = await f.Store.ReadAsync(Token); File.Delete(path);
            Click(window.FindControl<Button>("RetryWorkflowBendingFolders")!); await Until(() => !f.Model.IsBusy && !f.Model.HasBendingFolderProblem);
            Assert.Equal(before.Version, (await f.Store.ReadAsync(Token)).Version);
            Click(open); await Until(() => window.OpenedFolder is not null); Assert.Equal(path, window.OpenedFolder);
            Assert.Empty(window.OwnedWindows); Assert.Null(f.Model.Error); SaveFrame(window, "bending-ready-" + dark);
        }
        finally { window.Close(); }
    }

    private sealed class RecordingWindow(WorkflowPreferencesStore preferences) : WorkflowWindow(preferences)
    {
        public string? Copied { get; private set; }
        public string? OpenedFolder { get; private set; }
        public bool FailCopy { get; set; }
        protected override Task WriteCuttingTaskClipboardAsync(string text)
        { if (FailCopy) throw new IOException("Test clipboard failure"); Copied = text; return Task.CompletedTask; }
        protected override Task<bool> OpenFolderAsync(string path) { OpenedFolder = path; return Task.FromResult(true); }
    }
    private static Rect Bounds(Control control, Window window) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void InWindow(Control control, Window window)
    {
        var rect = Bounds(control, window); Assert.InRange(rect.Left, 0, window.ClientSize.Width); Assert.InRange(rect.Right, 0, window.ClientSize.Width);
        Assert.InRange(rect.Top, 0, window.ClientSize.Height); Assert.InRange(rect.Bottom, 0, window.ClientSize.Height);
    }
    private static void Click(Control control)
    {
        Pump();
        var root = TopLevel.GetTopLevel(control)!; var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(done()); }
    private static void SaveFrame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
    private sealed class BindingLog : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Errors { get; } = [];
        public BindingLog() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) => Errors.Add(messageTemplate + " " + string.Join("; ", propertyValues));
    }
}
