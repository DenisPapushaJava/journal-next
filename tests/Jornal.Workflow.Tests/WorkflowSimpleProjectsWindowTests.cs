using System.Text.Json;
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

public sealed class WorkflowSimpleProjectsWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task SelectingProjectShowsInlineFieldsAndKeepsOtherDraftsAndGlobalPrices(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var a = await f.AddAsync(); var b = await f.AddAsync("Плазма"); var pending = await f.AddAsync();
        await ProjectFixture.WriteAsync(a); await ProjectFixture.WriteAsync(b); await f.AcceptAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); prefs.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var grid = window.FindControl<DataGrid>("WorkflowProjectsGrid")!;
            var pane = window.FindControl<Border>("WorkflowProjectCalculation")!;
            var fields = window.FindControl<ScrollViewer>("WorkflowCalculationFields")!;
            var coefficient = window.FindControl<TextBox>("WorkflowCalculationCoefficient")!;
            var save = window.FindControl<Button>("SaveWorkflowSpecification")!;
            Assert.Equal(new[] { "Проекты", "Письмо" }, window.FindControl<TabControl>("WorkflowOrderTabs")!.Items.OfType<TabItem>().Select(t => t.Header));
            Assert.Null(window.FindControl<DataGrid>("WorkflowSpecificationsGrid")); Assert.False(DragDrop.GetAllowDrop(window));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>(), button => Equals(button.Content, "Текст"));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<JournalButton>(), button => button.Label is "Ручной ввод" or "Импорт вне проекта…" or "Сохранить тариф");
            Select(a.Id); Assert.True(pane.IsEffectivelyVisible); Assert.Empty(window.OwnedWindows);
            Assert.Equal(a.Id, f.Model.SelectedDraft!.Id);
            InViewport(window.FindControl<TextBox>("WorkflowCalculationMaterial")!, fields);
            InViewport(window.FindControl<TextBox>("WorkflowCalculationLength")!, fields);
            InWindow(save, window); InWindow(coefficient, window);
            Assert.DoesNotContain(pane.GetVisualDescendants().OfType<ComboBox>(), c => c.IsEffectivelyVisible);
            Assert.Empty(pane.GetVisualDescendants().OfType<Expander>());
            var firstDraft = f.Model.SelectedDraft; EditCoefficient("1,3"); Assert.Equal("1,3", firstDraft.Coefficient);
            Assert.True(f.Model.SelectedProject!.HasLocalChanges); Assert.Contains("не сохранены", f.Model.SelectedProject.State);
            var calculationRow = window.FindControl<Grid>("WorkflowProjectWorkspace")!.RowDefinitions[2];
            calculationRow.Height = new GridLength(1.7, GridUnitType.Star); Pump();
            Select(b.Id); Assert.Same(pane, window.FindControl<Border>("WorkflowProjectCalculation"));
            Assert.Equal(new GridLength(1.7, GridUnitType.Star), calculationRow.Height);
            Assert.True(firstDraft.IsDirty); Assert.Equal("1,3", firstDraft.Coefficient);
            Assert.True(f.Model.Projects.Single(p => p.Project.Id == a.Id).HasLocalChanges);
            EditCoefficient("1,4"); Assert.True(f.Model.SelectedProject!.HasLocalChanges);
            f.Model.CancelDraftCommand.Execute(null); Pump();
            Assert.False(f.Model.SelectedProject.HasLocalChanges); Assert.Equal("1", coefficient.Text);
            EditCoefficient("1,5"); Click(save);
            await Until(() => !f.Model.IsBusy && f.Model.SelectedDraft?.Saved?.Parameters.ManualCoefficient == 1.5m);
            Assert.Null(f.Model.Error); Assert.Same(firstDraft, f.Model.Drafts.Single(d => d.Id == a.Id));
            var height = grid.Bounds.Height;
            Select(pending.Id); Assert.Null(f.Model.SelectedDraft); Assert.True(pane.IsEffectivelyVisible); Assert.False(fields.IsEffectivelyVisible);
            Assert.Equal("Нет спецификации для этого проекта.", f.Model.CalculationEmptyText);
            Assert.InRange(Math.Abs(grid.Bounds.Height - height), 0, 1); SaveFrame("pending");
            Select(a.Id); Assert.Equal("1,3", coefficient.Text); Click(save);
            await Until(() => !f.Model.IsBusy && f.Model.SelectedDraft?.Saved?.Parameters.ManualCoefficient == 1.3m);
            Assert.Null(f.Model.Error); Assert.False(f.Model.HasPendingEdits); Assert.Empty(window.OwnedWindows);
            var after = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(JsonSerializer.Serialize(before.Rates), JsonSerializer.Serialize(after.Rates));
            Assert.Equal(3094m, after.Orders.Single().CuttingTotal);
            Assert.Equal(before.Orders.Single().Projects, after.Orders.Single().Projects);
            SaveFrame("ready"); Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors));

            void Select(Guid id)
            {
                var row = f.Model.Projects.Single(p => p.Project.Id == id); grid.ScrollIntoView(row, null); Pump();
                var cell = grid.GetVisualDescendants().OfType<TextBlock>().Single(t => ReferenceEquals(t.DataContext, row) && t.Classes.Contains("projectName") && t.IsEffectivelyVisible);
                Click(cell); Assert.Equal(id, f.Model.SelectedProject!.Project.Id);
            }
            void EditCoefficient(string value) { coefficient.Focus(); coefficient.SelectAll(); window.KeyTextInput(value); Pump(); }
            void SaveFrame(string state)
            {
                if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
                Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(output, $"simple-projects-{state}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally { window.Close(); }
    }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!; var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
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
        Assert.InRange(point.X, 0, window.ClientSize.Width - control.Bounds.Width); Assert.InRange(point.Y, 0, window.ClientSize.Height - control.Bounds.Height);
    }
    private static void InViewport(Control control, ScrollViewer viewport)
    {
        var point = control.TranslatePoint(default, viewport)!.Value;
        Assert.InRange(point.Y, 0, viewport.Bounds.Height - control.Bounds.Height); Assert.InRange(point.X, 0, viewport.Bounds.Width - control.Bounds.Width);
    }
    private sealed class BindingLog : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink; public List<string> Errors { get; } = [];
        public BindingLog() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string template) => Errors.Add(template);
        public void Log(LogEventLevel level, string area, object? source, string template, params object?[] values) => Errors.Add(template + " " + string.Join("; ", values));
    }
}
