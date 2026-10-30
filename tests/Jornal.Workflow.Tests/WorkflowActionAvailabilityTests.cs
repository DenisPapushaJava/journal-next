using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowActionAvailabilityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task MainActionsAndMenuFollowDataWithoutClicksOrLayoutChanges(bool dark, int width)
    {
        using var log = new WorkflowWindowTests.BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json")))
        { DataContext = f.Model, Width = width, Height = 768, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            Check(window, "OpenWorkflowResult", false); Check(window, "CopyWorkflowCuttingTask", false);
            Check(window, "RefreshWorkflowReports", false); Assert.False(f.Model.SaveHeaderCommand.CanExecute(null));
            MenuMatches(window, f.Model, false, false);
            f.Model.OrderTabIndex = 1; Pump(); Check(window, "GenerateWorkflowResult", false); Check(window, "CopyWorkflowResult", false);
            Assert.Equal(f.Model.GenerateResultHint, f.Model.ResultStatus); Assert.False(f.Model.HasResultStatusError);
            Frame(window, $"empty-result-{width}-{dark}");
            f.Model.OrderTabIndex = 0; Pump(); Click(window.FindControl<Button>("PrepareWorkflowProject")!);
            Check(window, "CreateWorkflowProject", false); f.Model.PlanThickness = "0"; Pump(); Check(window, "CreateWorkflowProject", false);
            f.Model.PlanThickness = "6"; Pump(); Check(window, "CreateWorkflowProject", true);
            Click(window.FindControl<Button>("CreateWorkflowProject")!); await Until(() => !f.Model.IsBusy && !f.Model.IsPlanning);
            Check(window, "RefreshWorkflowReports", true); Check(window, "OpenWorkflowResult", false);
            var project = f.Model.SelectedProject!.Project; await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); Pump();
            Check(window, "OpenWorkflowResult", true); Check(window, "CopyWorkflowCuttingTask", false);
            Assert.Contains("БЛЦБ", f.Model.SendToWorkHint); Assert.True(f.Model.GenerateResultCommand.CanExecute(null));
            MenuMatches(window, f.Model, true, false);
            Click(window.FindControl<Button>("OpenWorkflowResult")!); Assert.NotEmpty(f.Model.ResultText); Check(window, "CopyWorkflowResult", true);
            f.Model.SelectedDraft!.Coefficient = "1,5"; Pump();
            Check(window, "GenerateWorkflowResult", false); Check(window, "CopyWorkflowResult", true);
            Assert.True(f.Model.CanOpenResult); Assert.True(f.Model.CanSaveSpecification); Assert.True(f.Model.CanCancelDraft);
            Assert.False(f.Model.CanAddProject); Assert.False(f.Model.CanLoadReports); Assert.False(f.Model.CanMoveOrderToTrash);
            f.Model.ResultText = "Сохранить ручной текст"; Frame(window, $"dirty-result-{width}-{dark}");
            MenuMatches(window, f.Model, true, false);
            f.Model.OrderTabIndex = 0; Pump(); Click(window.FindControl<Button>("CancelWorkflowSpecification")!);
            Assert.True(f.Model.CanGenerateResult); Assert.False(f.Model.CanSaveSpecification); Assert.False(f.Model.CanCancelDraft);
            Assert.Equal("Сохранить ручной текст", f.Model.ResultText);
            f.Model.Blcb = "009337"; Pump(); Assert.True(f.Model.CanSaveHeader); Assert.False(f.Model.CanGenerateResult); Assert.False(f.Model.CanSendToWork);
            window.FindControl<Expander>("OrderHeaderExpander")!.IsExpanded = true; Pump(); Check(window, "SaveWorkflowHeader", true);
            Click(window.FindControl<Button>("SaveWorkflowHeader")!); await Until(() => !f.Model.IsBusy && !f.Model.CanSaveHeader);
            Check(window, "CopyWorkflowCuttingTask", true); MenuMatches(window, f.Model, true, true);
            Assert.Null(f.Model.ShopRoot); Assert.Contains("папку", f.Model.SendToWorkHint);
            f.Model.SelectedDraft!.Date = "не дата"; Pump(); Assert.False(f.Model.CanSaveSpecification); Assert.Contains("дату", f.Model.SaveSpecificationHint);
            f.Model.CancelDraftCommand.Execute(null); Assert.True(f.Model.CanGenerateResult);

            var before = JsonSerializer.Serialize(await f.Store.ReadAsync(Token));
            Click(window.FindControl<Button>("NewWorkflowOrder")!); Check(window, "CreateWorkflowOrder", false);
            f.Model.NewManager = "Иванов"; Pump(); Check(window, "CreateWorkflowOrder", false);
            f.Model.NewCustomer = "Новая заявка"; Pump(); Check(window, "CreateWorkflowOrder", true);
            Check(window, "NewWorkflowOrder", false); f.Model.NewOrderCommand.Execute(null);
            Assert.Equal("Новая заявка", f.Model.NewCustomer); f.Model.CancelNewOrderCommand.Execute(null);
            Assert.Equal(before, JsonSerializer.Serialize(await f.Store.ReadAsync(Token)));
            Assert.Equal("Сохранить ручной текст", f.Model.ResultText); Assert.Empty(log.Errors);
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
    }

    [Fact]
    public async Task PreparedProjectCanStillBeRetriedAndAvailabilityDoesNotReadTheDatabase()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); f.Model.AddProjectCommand.Execute(null); f.Model.PlanThickness = "6";
        using (File.Open(f.Store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            for (var i = 0; i < 10; i++)
            { Assert.True(f.Model.CanCreateProject); Assert.False(f.Model.CanGenerateResult); Assert.False(f.Model.CanSendToWork); Assert.NotEmpty(f.Model.OpenResultHint); }
            await f.Model.CreateProjectCommand.ExecuteAsync(null);
        }
        Assert.NotNull(f.Model.Error); Assert.True(f.Model.IsPlanning); Assert.False(f.Model.CanEditPlan);
        Assert.True(f.Model.CanCreateProject); Assert.Contains("Повторить", f.Model.CreateProjectHint); Assert.False(f.Model.CanCancelPlanning);
        var root = Path.Combine(f.DirectoryPath, "projects"); var prepared = Assert.Single(Directory.GetFiles(root, "*.rcam", SearchOption.AllDirectories));
        await f.Model.CreateProjectCommand.ExecuteAsync(null); Assert.Null(f.Model.Error); Assert.False(f.Model.IsPlanning);
        Assert.Equal(prepared, f.Model.SelectedProject!.Project.NativePath); Assert.Single(Directory.GetFiles(root, "*.rcam", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingRootAndInvalidThicknessDisableCreationUntilSettingsAreSaved()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        using (var settings = f.Model.CreateGeneralSettings())
        { settings.SelectedSectionIndex = 1; settings.ProjectRoot = ""; await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.Error); }
        f.Model.AddProjectCommand.Execute(null); f.Model.PlanThickness = "6";
        Assert.False(f.Model.CanCreateProject); Assert.Contains("Хранилище", f.Model.CreateProjectHint); Assert.True(f.Model.CanCancelPlanning);
        using (var settings = f.Model.CreateGeneralSettings())
        { settings.SelectedSectionIndex = 1; settings.ProjectRoot = Path.Combine(f.DirectoryPath, "projects"); await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.Error); }
        Assert.True(f.Model.CanCreateProject);
        foreach (var value in new[] { "", "0", "-1", "6 мм", "1,2,3" }) { f.Model.PlanThickness = value; Assert.False(f.Model.CanCreateProject); }
        f.Model.PlanThickness = "1,5"; Assert.True(f.Model.CanCreateProject);
    }

    [Fact]
    public async Task ExcludedPositionsVariantsAndAuthorUseTheSavedComposition()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var first = await f.AddAsync(); var second = await f.AddAsync("Плазма");
        await ProjectFixture.WriteAsync(first); await ProjectFixture.WriteAsync(second); await f.AcceptAsync();
        f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        Assert.True(f.Model.CanGenerateResult); Assert.True(f.Model.CanSendToWork);
        await f.Model.SetProjectIncludedAsync(first.Id, false); Assert.True(f.Model.CanGenerateResult);
        await f.Model.SetProjectIncludedAsync(second.Id, false); Assert.False(f.Model.CanGenerateResult); Assert.False(f.Model.CanSendToWork);
        f.Model.ResultText = "Готовый ручной результат"; Assert.True(f.Model.CanOpenResult); Assert.True(f.Model.CanCopyResult);
        f.Model.ResultText = ""; Assert.False(f.Model.CanOpenResult);
        await f.Model.SetProjectIncludedAsync(first.Id, true); Assert.True(f.Model.CanGenerateResult);
        f.Model.SelectedProject = f.Model.Projects.Single(p => p.Project.Id == first.Id);
        await f.AddAsync(redo: true); Assert.False(f.Model.CanGenerateResult); Assert.Contains("вариант", f.Model.GenerateResultHint);
        f.Model.SelectedProject = f.Model.Projects.Single(p => p.Project.Id == first.Id);
        await f.Model.SelectProjectForTotalCommand.ExecuteAsync(null); Assert.True(f.Model.CanGenerateResult); Assert.True(f.Model.CanSendToWork);
        var employee = f.Model.CurrentEmployee; f.Model.CurrentEmployee = null;
        Assert.True(f.Model.CanGenerateResult); Assert.False(f.Model.CanSendToWork); Assert.False(f.Model.CanLoadReports); Assert.False(f.Model.CanStartNewOrder); Assert.Contains("Технолог", f.Model.SendToWorkHint);
        f.Model.CurrentEmployee = employee; Assert.True(f.Model.CanSendToWork);
    }

    private static void Check(Window window, string name, bool enabled)
    { Pump(); var control = window.FindControl<Button>(name)!; Assert.Equal(enabled, control.IsEffectivelyEnabled); Assert.NotNull(ToolTip.GetTip(control)); Assert.True(ToolTip.GetShowOnDisabled(control)); }
    private static void MenuMatches(Window window, WorkflowViewModel model, bool result, bool send)
    {
        var grid = window.FindControl<DataGrid>("WorkflowOrdersGrid")!;
        var row = grid.GetVisualDescendants().OfType<DataGridRow>().Single(r => r.DataContext is WorkflowOrderRow o && o.Order.Id == model.SelectedOrder!.Order.Id);
        var point = row.TranslatePoint(new Point(40, row.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point); window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); Pump();
        var menu = grid.ContextMenu!; Assert.True(menu.IsOpen);
        foreach (var (name, enabled) in new[] { ("WorkflowOrderMenuResult", result), ("WorkflowOrderMenuSend", send), ("WorkflowOrderMenuTrash", model.CanMoveOrderToTrash) })
        { var item = menu.Items.OfType<MenuItem>().Single(i => i.Name == name); Assert.Equal(enabled, item.IsEnabled); Assert.NotNull(ToolTip.GetTip(item)); Assert.True(ToolTip.GetShowOnDisabled(item)); }
        menu.Close(); Pump();
    }
    private static void Click(Control c)
    { var root = TopLevel.GetTopLevel(c)!; var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), root)!.Value; root.MouseDown(p, MouseButton.Left); root.MouseUp(p, MouseButton.Left); Pump(); }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> ready)
    { for (var i = 0; i < 500; i++) { Pump(); if (ready()) return; await Task.Delay(10, Token); } Assert.True(ready()); }
    private static void Frame(Window w, string name)
    { if (Environment.GetEnvironmentVariable("WORKFLOW_ACTION_SCREENSHOTS") is not { } output) return; Directory.CreateDirectory(output); w.CaptureRenderedFrame()!.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
}
