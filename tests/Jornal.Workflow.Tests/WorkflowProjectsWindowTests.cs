using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowProjectsWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1366)] [InlineData(true, 1366)]
    [InlineData(false, 1180)] [InlineData(true, 1180)]
    public async Task ProjectsPlanningAndBulkReviewFitDesktopWidthsInBothThemes(bool dark, int windowWidth)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var creatorProfile = f.Model.CreateGeneralSettings(); creatorProfile.SelectProfileColorCommand.Execute(creatorProfile.FrequentColors[1]);
        await creatorProfile.SaveCommand.ExecuteAsync(null); Assert.Null(creatorProfile.Error);
        var a = await f.AddAsync(); await f.AddAsync("Плазма");
        await ProjectFixture.WriteAsync(a); await f.AcceptAsync();
        var draft = f.Model.SelectedDraft!; draft.Length = "15"; draft.Coefficient = "1,2";
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var originalId = f.Model.SelectedOrder!.Order.Id;
        foreach (var name in new[] { "ООО «Север»", "ООО «Альфа»" })
        {
            var profile = f.Model.CreateGeneralSettings(); profile.ProfileName = name.Contains("Север", StringComparison.Ordinal) ? "Иванов" : "Сидоров";
            profile.SelectProfileColorCommand.Execute(profile.FrequentColors[name.Contains("Север", StringComparison.Ordinal) ? 0 : 2]);
            await profile.SaveCommand.ExecuteAsync(null); Assert.Null(profile.Error);
            f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Иванов"; f.Model.NewCustomer = name; await f.Model.CreateOrderCommand.ExecuteAsync(null);
        }
        f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == originalId);
        await ProjectFixture.WriteAsync(a, 20000);
        var review = await f.ReviewAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Save(new(dark) { EmployeeId = WorkflowTestData.CreateWorkspace().Employees[1].Id });
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, Width = windowWidth, Height = 768 };
        var reviewWindow = new WorkflowReportReviewWindow { DataContext = review, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); while (f.Model.IsBusy) await Task.Delay(10, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);
            var orderRow = f.Model.Orders.Single(r => r.Order.Id == originalId);
            Assert.StartsWith("Заявка " + orderRow.Number + " · ", f.Model.OrderTitle);
            InWindow(window.FindControl<TextBlock>("WorkflowOrderTitle")!, window);
            var orderTexts = window.FindControl<DataGrid>("WorkflowOrdersGrid")!.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => ReferenceEquals(t.DataContext, orderRow)).Select(t => t.Text).ToArray();
            Assert.Contains(orderRow.NumberDate, orderTexts); Assert.Contains(orderRow.NumberTime, orderTexts);
            var markers = window.FindControl<DataGrid>("WorkflowOrdersGrid")!.GetVisualDescendants().OfType<Border>()
                .Where(b => b.Name == "WorkflowCreatorMarker").ToArray();
            Assert.Equal(3, markers.Length);
            Assert.Equal(3, f.Model.Orders.Select(r => r.Creator!.Id).Distinct().Count());
            foreach (var marker in markers)
            {
                InWindow(marker, window); Assert.Equal(6, marker.Bounds.Width); Assert.True(marker.Bounds.Height >= 26);
                var row = Assert.IsType<WorkflowOrderRow>(marker.DataContext);
                Assert.Equal(row.Creator!.ColorArgb, Assert.IsType<Avalonia.Media.SolidColorBrush>(marker.Background).Color.ToUInt32());
                Assert.Contains(row.CreatorDescription, ToolTip.GetTip(Assert.IsType<Grid>(marker.Parent))!.ToString());
            }
            f.Model.Search = orderRow.Number; Assert.Equal(originalId, Assert.Single(f.Model.Orders).Order.Id);
            f.Model.Search = orderRow.NumberCaption; Assert.Equal(originalId, Assert.Single(f.Model.Orders).Order.Id);
            f.Model.Search = orderRow.NumberDate + " " + orderRow.NumberTime; Assert.Equal(originalId, Assert.Single(f.Model.Orders).Order.Id);
            f.Model.Search = orderRow.NumberDate; Assert.Equal(3, f.Model.Orders.Count);
            f.Model.Search = ""; Dispatcher.UIThread.RunJobs();
            var grid = window.FindControl<DataGrid>("WorkflowProjectsGrid")!; Assert.Equal(2, grid.ItemsSource!.Cast<object>().Count());
            foreach (var button in grid.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is WorkflowProjectRow))
                InWindow(button, window);
            f.Model.AddProjectCommand.Execute(null); f.Model.PlanThickness = "2";
            Dispatcher.UIThread.RunJobs();
            InWindow(window.FindControl<Control>("WorkflowProjectThickness")!, window);
            InWindow(window.FindControl<ComboBox>("WorkflowProjectMachine")!, window);
            var machineInput = window.FindControl<ComboBox>("WorkflowProjectMachine")!;
            var thicknessInput = window.FindControl<Control>("WorkflowProjectThickness")!;
            Assert.True(machineInput.Bounds.Width < window.FindControl<Border>("WorkflowProjectPlan")!.Bounds.Width / 2);
            Assert.True(thicknessInput.Bounds.Width < machineInput.Bounds.Width / 2);
            var create = window.FindControl<Button>("CreateWorkflowProject")!; var cancel = window.FindControl<Button>("CancelWorkflowProjectPlan")!;
            InWindow(create, window); InWindow(cancel, window);
            Assert.Equal(Bounds(machineInput, window).Top, Bounds(thicknessInput, window).Top);
            Assert.Equal(Bounds(machineInput, window).Top, Bounds(create, window).Top);
            Assert.True(Bounds(machineInput, window).Right < Bounds(thicknessInput, window).Left);
            Assert.True(Bounds(thicknessInput, window).Right < Bounds(create, window).Left);
            Assert.Null(window.FindControl<Control>("WorkflowProjectMaterial"));
            InWindow(window.FindControl<Button>("RefreshWorkflowReports")!, window);
            Assert.True(window.FindControl<DataGrid>("WorkflowOrdersGrid")!.IsEffectivelyVisible);
            var mainList = Bounds(window.FindControl<DataGrid>("WorkflowOrdersGrid")!, window);
            var mainCard = Bounds(window.FindControl<Grid>("WorkflowOrderCard")!, window);
            Assert.True(mainList.Right < mainCard.Left);
            SaveFrame(window, dark, "workflow-projects");
            f.Model.CancelPlanningCommand.Execute(null);
            Dispatcher.UIThread.RunJobs(); SaveFrame(window, dark, "workflow-journal");
            window.FindControl<TabControl>("WorkflowOrderTabs")!.SelectedIndex = 1;
            f.Model.ResultText = "Результат для письма\n"; Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, f.Model.OrderTabIndex);
            f.Model.OrderTabIndex = 0; Dispatcher.UIThread.RunJobs();
            InWindow(window.FindControl<Button>("SaveWorkflowSpecification")!, window);
            SaveFrame(window, dark, "workflow-order-details");
            Assert.Equal("Результат для письма\n", f.Model.ResultText);
            f.Model.ResultText = ""; f.Model.IsResultExpanded = false;
            window.FindControl<TabControl>("WorkflowOrderTabs")!.SelectedIndex = 0;
            reviewWindow.Show(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, review.Candidates.Count); Assert.Single(review.Selected!.Conflicts);
            Assert.False(review.SaveCommand.CanExecute(null));
            InWindow(reviewWindow.FindControl<DataGrid>("WorkflowReportCandidates")!, reviewWindow);
            InWindow(reviewWindow.FindControl<Button>("SaveWorkflowReports")!, reviewWindow);
            var list = reviewWindow.FindControl<DataGrid>("WorkflowReportCandidates")!;
            var panel = reviewWindow.FindControl<Border>("ReviewDetailsPanel")!;
            Assert.True(Bounds(list, reviewWindow).Right < Bounds(panel, reviewWindow).Left);
            Assert.All(list.GetVisualDescendants().OfType<DataGridRow>(), row => Assert.True(row.Bounds.Height > 60));
            InWindow(reviewWindow.FindControl<TextBox>("ReviewCoefficient")!, reviewWindow);
            SaveFrame(reviewWindow, dark, "workflow-report-review");
            var original = review.Selected; original.Draft!.Coefficient = "1,4";
            var other = review.Candidates.Single(c => c != original);
            var checkbox = list.GetVisualDescendants().OfType<CheckBox>().Single(c => c.DataContext == other);
            var point = Bounds(checkbox, reviewWindow).Center;
            reviewWindow.MouseDown(point, MouseButton.Left); reviewWindow.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.True(other.IsSelected); Assert.Same(original, review.Selected);
            var otherRow = list.GetVisualDescendants().OfType<DataGridRow>().Single(row => row.DataContext == other);
            point = Bounds(otherRow, reviewWindow).Center;
            reviewWindow.MouseDown(point, MouseButton.Left); reviewWindow.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.Same(other, review.Selected);
            var originalRow = list.GetVisualDescendants().OfType<DataGridRow>().Single(row => row.DataContext == original);
            point = Bounds(originalRow, reviewWindow).Center;
            reviewWindow.MouseDown(point, MouseButton.Left); reviewWindow.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.Same(original, review.Selected); Assert.Equal("1,4", review.Selected.Draft!.Coefficient);
            review.Selected.Conflicts[0].Selection = 1; Assert.True(review.SaveCommand.CanExecute(null));
            var splitter = reviewWindow.FindControl<GridSplitter>("ReviewPaneSplitter")!;
            var width = panel.Bounds.Width; var grip = Bounds(splitter, reviewWindow).Center;
            reviewWindow.MouseDown(grip, MouseButton.Left);
            reviewWindow.MouseMove(grip + new Vector(40, 0), RawInputModifiers.LeftMouseButton);
            reviewWindow.MouseUp(grip + new Vector(40, 0), MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Assert.True(panel.Bounds.Width < width); InWindow(panel, reviewWindow);
            Dispatcher.UIThread.RunJobs();
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors.Distinct().Take(12)));
        }
        finally { reviewWindow.Close(); window.Close(); }
    }
    private static void InWindow(Control control, Window window)
    {
        var point = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(point.X, 0, window.ClientSize.Width);
        Assert.InRange(point.Y, 0, window.ClientSize.Height);
        Assert.InRange(point.X + control.Bounds.Width, 0, window.ClientSize.Width);
        Assert.InRange(point.Y + control.Bounds.Height, 0, window.ClientSize.Height);
    }
    private static Rect Bounds(Control control, Window window) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void SaveFrame(Window window, bool dark, string name)
    {
        if (window.Width < 1200) name += "-1180";
        var image = window.CaptureRenderedFrame(); Assert.NotNull(image);
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is { } output)
        { Directory.CreateDirectory(output); image!.Save(Path.Combine(output, name + (dark ? "-dark.png" : "-light.png")), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
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
