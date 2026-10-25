using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class ProjectMaintenanceWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1366)] [InlineData(true, 1366)]
    [InlineData(false, 1180)] [InlineData(true, 1180)]
    public async Task RowMenuEditorFailureDialogInclusionTrashAndRestoreWorkInBothThemes(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        prefs.Save(prefs.Load() with { DarkTheme = dark });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Pump();
            var grid = window.FindControl<DataGrid>("WorkflowProjectsGrid")!;
            var actions = RowActions(window); InBounds(actions, grid);
            var inclusion = RowCheck(window); InBounds(inclusion, grid);
            var activeName = grid.GetVisualDescendants().OfType<TextBlock>().Single(t => t.IsEffectivelyVisible && t.Classes.Contains("projectName"));
            var activeColor = Assert.IsType<SolidColorBrush>(activeName.Foreground).Color;
            var activeBackground = Assert.IsType<SolidColorBrush>(ProjectRow(grid).Background).Color;
            Assert.DoesNotContain("workflowProjectExcluded", ProjectRow(grid).Classes);
            Assert.Equal(FontWeight.SemiBold, activeName.FontWeight);
            Assert.True(ProjectRow(grid).Bounds.Height > 40);
            Click(actions);
            var menu = Assert.IsType<MenuFlyout>(actions.Flyout); Assert.True(menu.IsOpen);
            var edit = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Изменить параметры…"));
            Assert.IsType<WorkflowProjectRow>(edit.DataContext); Click(edit); Pump();
            var editor = Assert.IsType<WorkflowProjectEditorWindow>(Assert.Single(window.OwnedWindows));
            var model = Assert.IsType<WorkflowProjectEditorViewModel>(editor.DataContext);
            Assert.True(editor.FindControl<EntrySuggestionComboBox>("EditWorkflowProjectThickness")!.IsKeyboardFocusWithin);
            model.Thickness = "8"; Pump(); Assert.StartsWith("8 мм ", model.PreviewName); SaveFrame(editor, "editor", dark, width);
            Click(editor.FindControl<Button>("SaveWorkflowProjectParameters")!); await Until(() => window.OwnedWindows.Count == 0);
            var updated = Assert.Single(f.Model.Projects).Project;
            Assert.Equal(project.Id, updated.Id); Assert.StartsWith("8 мм ", updated.Name); Assert.True(File.Exists(updated.NativePath));

            // Files appeared since creation: the actual save is blocked, with a modal and preserved input.
            await ProjectFixture.WriteAsync(updated);
            actions = RowActions(window); Click(actions);
            Click(((MenuFlyout)actions.Flyout!).Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Изменить параметры…")));
            editor = Assert.IsType<WorkflowProjectEditorWindow>(Assert.Single(window.OwnedWindows));
            model = Assert.IsType<WorkflowProjectEditorViewModel>(editor.DataContext); model.Thickness = "10"; Pump();
            Click(editor.FindControl<Button>("SaveWorkflowProjectParameters")!); await Until(() => editor.OwnedWindows.Count == 1);
            var failure = Assert.IsType<WorkflowProjectActionWindow>(Assert.Single(editor.OwnedWindows));
            Assert.Equal("Не удалось изменить проект", failure.Title);
            Assert.Contains("рабочие файлы", failure.FindControl<TextBlock>("ActionMessage")!.Text);
            Assert.Equal("Вернуться к редактированию", failure.FindControl<JournalButton>("CancelProjectAction")!.Label);
            SaveFrame(failure, "blocked", dark, width);
            failure.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Assert.Empty(editor.OwnedWindows); Assert.True(editor.IsVisible); Assert.Equal("10", model.Thickness); Assert.True(model.CanEdit);
            File.Delete(ProjectFixture.ReportPath(updated));
            Click(editor.FindControl<Button>("SaveWorkflowProjectParameters")!); await Until(() => window.OwnedWindows.Count == 0);
            updated = Assert.Single(f.Model.Projects).Project; Assert.StartsWith("10 мм ", updated.Name);

            Click(RowCheck(window)); await Until(() => !f.Model.IsBusy);
            Assert.False(Assert.Single(f.Model.Projects).IsIncluded); Assert.Contains("Исключён", f.Model.Projects[0].State);
            var excludedInfo = grid.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Classes.Contains("workflowProjectInfo")).ToArray();
            Assert.Equal(6, excludedInfo.Length);
            Assert.Equal("—", excludedInfo.Single(t => t.Classes.Contains("projectChamfer")).Text);
            Assert.Equal("—", excludedInfo.Single(t => t.Classes.Contains("projectBending")).Text);
            Assert.All(excludedInfo, text =>
            {
                Assert.Contains("excluded", text.Classes); Assert.Equal(FontWeight.Normal, text.FontWeight);
                Assert.NotEqual(activeColor, Assert.IsType<SolidColorBrush>(text.Foreground).Color);
            });
            Assert.True(RowCheck(window).IsEffectivelyEnabled); Assert.True(RowActions(window).IsEffectivelyEnabled);
            Assert.Contains("workflowProjectExcluded", ProjectRow(grid).Classes);
            Assert.Equal(40, ProjectRow(grid).Bounds.Height);
            var compact = grid.GetVisualDescendants().OfType<Grid>().Single(g => g.Name == "WorkflowCompactProject");
            Assert.True(compact.IsEffectivelyVisible); Assert.Contains("Исключён", excludedInfo.Select(t => t.Text));
            Assert.Equal(f.Model.Projects[0].DetailsHint, ToolTip.GetTip(compact));
            var compactName = excludedInfo.Single(t => t.Classes.Contains("projectName"));
            Assert.Equal(TextTrimming.CharacterEllipsis, compactName.TextTrimming); Assert.Equal(1, compactName.MaxLines);
            var excludedBackground = Assert.IsType<SolidColorBrush>(ProjectRow(grid).Background).Color;
            Assert.NotEqual(activeBackground, excludedBackground); Assert.Equal(1, ProjectRow(grid).Opacity);
            Assert.Equal(1, RowCheck(window).Opacity); Assert.Equal(1, RowActions(window).Opacity);
            Assert.True(grid.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is WorkflowProjectRow && b.Content is PathIcon).IsEffectivelyEnabled);
            SaveFrame(window, "excluded", dark, width);
            f.Model.SelectedProject = null; Pump();
            Assert.Equal(40, ProjectRow(grid).Bounds.Height);
            Assert.NotEqual(excludedBackground, Assert.IsType<SolidColorBrush>(ProjectRow(grid).Background).Color);
            SaveFrame(window, "excluded-unselected", dark, width);
            f.Model.SelectedProject = Assert.Single(f.Model.Projects); Pump();
            Assert.Equal(40, ProjectRow(grid).Bounds.Height);
            var switchTheme = window.FindControl<ToggleButton>("ThemeSwitch")!;
            switchTheme.IsChecked = !dark; Pump();
            Assert.NotEqual(excludedBackground, Assert.IsType<SolidColorBrush>(ProjectRow(grid).Background).Color);
            switchTheme.IsChecked = dark; Pump();
            Assert.Equal(excludedBackground, Assert.IsType<SolidColorBrush>(ProjectRow(grid).Background).Color);
            actions = RowActions(window); Click(actions);
            Click(((MenuFlyout)actions.Flyout!).Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Убрать в корзину…")));
            var confirmation = Assert.IsType<WorkflowProjectActionWindow>(Assert.Single(window.OwnedWindows));
            Assert.Contains("файлы останутся", confirmation.FindControl<TextBlock>("ActionMessage")!.Text);
            Click(confirmation.FindControl<Button>("ConfirmProjectAction")!); await Until(() => !f.Model.IsBusy && window.OwnedWindows.Count == 0);
            Assert.Empty(f.Model.Projects); Assert.True(File.Exists(updated.NativePath));
            Click(window.FindControl<CheckBox>("WorkflowProjectTrashToggle")!); Pump();
            Assert.True(Assert.Single(f.Model.Projects).IsDeleted); SaveFrame(window, "trash", dark, width);
            actions = RowActions(window); Click(actions);
            Click(((MenuFlyout)actions.Flyout!).Items.OfType<MenuItem>().Single(i => Equals(i.Header, "Восстановить проект")));
            await Until(() => !f.Model.IsBusy); Assert.False(f.Model.ShowTrashedProjects);
            Assert.False(Assert.Single(f.Model.Projects).IsDeleted); Assert.False(f.Model.Projects[0].IsIncluded);
            Click(RowCheck(window)); await Until(() => !f.Model.IsBusy); Assert.True(f.Model.Projects[0].IsIncluded);
            var restoredName = grid.GetVisualDescendants().OfType<TextBlock>().Single(t => t.IsEffectivelyVisible && t.Classes.Contains("projectName"));
            Assert.DoesNotContain("excluded", restoredName.Classes); Assert.Equal(FontWeight.SemiBold, restoredName.FontWeight);
            Assert.Equal(activeColor, Assert.IsType<SolidColorBrush>(restoredName.Foreground).Color);
            Assert.DoesNotContain("workflowProjectExcluded", ProjectRow(grid).Classes);
            Assert.True(ProjectRow(grid).Bounds.Height > 40);
            Assert.True(File.Exists(updated.NativePath)); Assert.Null(f.Model.Error);
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors.Distinct()));
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToArray())
            { foreach (var child in owned.OwnedWindows.ToArray()) child.Close(); owned.Close(); }
            window.Close();
        }
    }

    private static Button RowActions(Window window) => window.GetVisualDescendants().OfType<Button>().Single(b => b.Flyout is MenuFlyout && b.DataContext is WorkflowProjectRow);
    private static CheckBox RowCheck(Window window) => window.GetVisualDescendants().OfType<CheckBox>().Single(b => b.DataContext is WorkflowProjectRow);
    private static DataGridRow ProjectRow(DataGrid grid) => grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.DataContext is WorkflowProjectRow);
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
        Pump(); Assert.True(done(), "UI operation did not finish");
    }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!; var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void InBounds(Control control, Control container)
    {
        var point = control.TranslatePoint(default, container)!.Value;
        Assert.True(control.IsEffectivelyVisible); Assert.InRange(point.X, 0, container.Bounds.Width);
        Assert.InRange(point.X + control.Bounds.Width, 0, container.Bounds.Width);
    }
    private static void SaveFrame(TopLevel window, string name, bool dark, int width)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } path) return;
        Directory.CreateDirectory(path); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        frame.Save(Path.Combine(path, $"project-{name}-{width}-{(dark ? "dark" : "light")}.png"), new PngBitmapEncoderOptions());
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
