using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowInclusionWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1366)] [InlineData(true, 1366)]
    [InlineData(false, 1180)] [InlineData(true, 1180)]
    public async Task PendingToggleKeepsWorkspaceRowFocusAndScrollWithoutResettingTheCard(bool dark, int width)
    {
        using var log = new BindingLog(); var gate = new InclusionWriterGate(); using var f = gate.Fixture(); await f.InitializeAsync();
        for (var i = 0; i < 6; i++) await f.AddAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var variant = await f.AddAsync("Плазма", redo: true);
        for (var i = 0; i < 7; i++) await f.AddAsync();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); prefs.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var grid = window.FindControl<DataGrid>("WorkflowProjectsGrid")!;
            var workspace = window.FindControl<Grid>("JournalWorkspace")!;
            var target = f.Model.Projects.Single(p => p.Project.Id == project.Id);
            f.Model.SelectedProject = target; f.Model.ResultText = "Сохранить моё письмо"; f.Model.OrderTabIndex = 0; Pump();
            grid.ScrollIntoView(target, null); Pump();
            var check = Check(grid, target); var visualRow = check.FindAncestorOfType<DataGridRow>()!;
            var scroll = grid.GetVisualDescendants().OfType<ScrollBar>().First(s => s.Orientation == Orientation.Vertical);
            Assert.True(scroll.Value > 0); var offset = scroll.Value;
            var draft = f.Model.SelectedDraft; var savedCalculation = f.Model.SelectedDraft?.Saved; var orderRow = f.Model.SelectedOrder;
            var rows = f.Model.Projects.ToArray(); var resets = new List<string>(); WorkflowInclusionUpdateTests.Observe(f.Model, resets);
            var enabledChanges = new List<bool>();
            workspace.PropertyChanged += (_, e) => { if (e.Property.Name == nameof(Control.IsEnabled)) enabledChanges.Add(workspace.IsEnabled); };
            Click(check); await gate.Entered.Task.WaitAsync(TestContext.Current.CancellationToken); Pump();
            Assert.True(f.Model.IsBusy); Assert.True(workspace.IsEffectivelyEnabled); Assert.True(grid.IsEffectivelyEnabled);
            Assert.True(check.IsFocused); Assert.True(check.IsEffectivelyEnabled); Assert.False(check.IsHitTestVisible);
            Assert.False(check.IsChecked); Assert.True(target.IsIncluded); Assert.True(visualRow.Bounds.Height > 40);
            var newOrder = window.GetVisualDescendants().OfType<JournalButton>().Single(b => b.Label == "Новая заявка");
            Assert.True(newOrder.IsEffectivelyEnabled); Assert.Equal(1, newOrder.Opacity);
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null); Pump();
            Assert.False(check.IsChecked); Assert.Equal(1, gate.Calls);
            window.Close(); Assert.True(window.IsVisible); Save(window, dark, width, "pending");
            gate.Release.TrySetResult(); await Until(() => !f.Model.IsBusy); Pump();
            Assert.Null(f.Model.Error); Assert.Empty(resets); Assert.DoesNotContain(false, enabledChanges);
            Assert.Same(check, Check(grid, target)); Assert.Same(visualRow, check.FindAncestorOfType<DataGridRow>());
            Assert.True(check.IsFocused); Assert.True(check.IsHitTestVisible); Assert.False(check.IsChecked);
            Assert.Equal(40, visualRow.Bounds.Height); Assert.InRange(Math.Abs(scroll.Value - offset), 0, 1);
            Assert.Equal(rows, f.Model.Projects); Assert.Same(target, f.Model.SelectedProject);
            Assert.Same(draft, f.Model.SelectedDraft); Assert.Same(savedCalculation, f.Model.SelectedDraft?.Saved); Assert.Same(orderRow, f.Model.SelectedOrder);
            Assert.False(f.Model.Projects.Single(p => p.Project.Id == variant.Id).IsIncluded);
            Assert.Equal("Сохранить моё письмо", f.Model.ResultText); Assert.True(f.Model.ResultIsStale); Save(window, dark, width, "confirmed");
            Click(check); await Until(() => !f.Model.IsBusy); Pump();
            Assert.Null(f.Model.Error); Assert.True(check.IsChecked); Assert.True(check.IsFocused); Assert.True(visualRow.Bounds.Height > 40);
            Assert.Same(check, Check(grid, target)); Assert.Equal(2, gate.Calls); Assert.Empty(resets);
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors.Distinct()));
        }
        finally
        {
            gate.Release.TrySetResult(); await Until(() => !f.Model.IsBusy); window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FailedToggleRestoresCheckboxWithoutDestroyingTheFocusedControl()
    {
        var gate = new InclusionWriterGate { Fail = true }; using var f = gate.Fixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json"))) { DataContext = f.Model, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var grid = window.FindControl<DataGrid>("WorkflowProjectsGrid")!; var row = Assert.Single(f.Model.Projects);
            Assert.Equal(project.Id, row.Project.Id); var check = Check(grid, row);
            Click(check); await gate.Entered.Task.WaitAsync(TestContext.Current.CancellationToken); Assert.False(check.IsChecked);
            gate.Release.TrySetResult(); await Until(() => !f.Model.IsBusy);
            Assert.Same(check, Check(grid, row)); Assert.True(check.IsChecked); Assert.True(check.IsFocused);
            Assert.True(row.IsIncluded); Assert.Contains("подтвердить", f.Model.Error); Assert.Equal(1, gate.Calls);
            Assert.True(window.FindControl<Grid>("JournalWorkspace")!.IsEffectivelyEnabled);
        }
        finally { gate.Release.TrySetResult(); await Until(() => !f.Model.IsBusy); window.Close(); }
    }

    private static CheckBox Check(DataGrid grid, WorkflowProjectRow row) => grid.GetVisualDescendants().OfType<CheckBox>().Single(b => ReferenceEquals(b.DataContext, row));
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
    private static void Save(TopLevel root, bool dark, int width, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); root.CaptureRenderedFrame()!.Save(Path.Combine(output, $"inclusion-{name}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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
