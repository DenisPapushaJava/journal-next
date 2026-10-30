using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowMapSigningWindowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [AvaloniaTheory]
    [InlineData(false, "Лазер")]
    [InlineData(true, "Лазер")]
    [InlineData(false, "ГАР")]
    [InlineData(true, "ГАР")]
    public async Task SigningButtonsRequireSavedBlcbAndExplainDisabledState(bool dark, string machine)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); await f.AddAsync(machine);
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = 1180, Height = 768 }; window.Show();
        try
        {
            await Until(() => !f.Model.IsBusy); var button = window.FindControl<JournalButton>("SignWorkflowMaps")!;
            Assert.False(button.IsEnabled); Assert.False(Assert.Single(f.Model.Projects).CanSignMaps);
            Assert.Contains("БЛЦБ", f.Model.MapSigningHint); Assert.Contains("БЛЦБ", Assert.Single(f.Model.Projects).MapSigningHint);
            Frame(window, "map-signing-no-blcb-" + machine + "-" + dark);
            f.Model.Blcb = "009337"; Pump(); Assert.False(button.IsEnabled);
            await f.Model.SaveHeaderCommand.ExecuteAsync(null); Pump(); Assert.Null(f.Model.Error);
            Assert.True(button.IsEnabled); Assert.True(Assert.Single(f.Model.Projects).CanSignMaps);
            f.Model.Blcb = ""; Pump(); Assert.False(button.IsEnabled); Assert.False(Assert.Single(f.Model.Projects).CanSignMaps);
            await f.Model.SignMapsAsync(token: Token); Assert.Contains("БЛЦБ", Assert.Single(f.Model.MapSigningResults).Message); Assert.Null(f.Model.Error);
            await f.Model.SaveHeaderCommand.ExecuteAsync(null); Pump(); Assert.False(button.IsEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AwjcUsesExistingToolbarAndShowsNoOpOnSecondClick(bool dark)
    {
        using var log = new WorkflowWindowTests.BindingLog(); using var f = new ProjectFixture();
        await f.InitializeAsync(); var project = await f.AddAsync("ГАР");
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single(); var actor = f.Model.CurrentEmployee!.Id;
        var parameters = new CuttingParameters("ГАР", "Ст3", "Базовая", 6, "Лист", 1, 10, 2, 12, 60, 1, false, null) { MachineId = project.MachineId };
        await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.SaveSpecification(w, order.Id, order.Version,
            project.Id, parameters, "1029-121314", new(2026, 10, 29), actor, DateTimeOffset.UtcNow, profile: project.ImportProfile), Token);
        await f.Model.ReloadCommand.ExecuteAsync(null); f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        using var maps = new MapSigningFixture("ГАР");
        var path = Path.Combine(Path.GetDirectoryName(project.NativePath)!, "Карта_pp.pdf");
        File.Copy(WaterjetMapFixture.Create(maps, native: Path.GetFileName(project.NativePath)), path);
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Assert.Null(f.Model.Error);
            var geometry = Geometry(window);
            var button = window.FindControl<JournalButton>("SignWorkflowMaps")!; Assert.True(button.IsEnabled);
            Assert.Contains("AWJC", Assert.Single(f.Model.Projects).MapSigningHint);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !f.Model.IsBusy && f.Model.HasMapSigningResults);
            Assert.Equal(MapSigningStatus.Signed, Assert.Single(f.Model.MapSigningResults).Status);
            var bytes = await File.ReadAllBytesAsync(path, Token); var time = File.GetLastWriteTimeUtc(path);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => !f.Model.IsBusy && f.Model.HasMapSigningResults);
            Assert.Equal(MapSigningStatus.Unchanged, Assert.Single(f.Model.MapSigningResults).Status);
            Assert.Equal(geometry, Geometry(window));
            Assert.Equal(time, File.GetLastWriteTimeUtc(path)); Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token));
            Frame(window, "awjc-map-signing-" + dark); Assert.Empty(log.Errors);
        }
        finally { window.Close(); }
    }
    [AvaloniaTheory]
    [InlineData(false, 1180)]
    [InlineData(true, 1180)]
    [InlineData(false, 1366)]
    [InlineData(true, 1366)]
    public async Task ToolbarSignsIncludedProjectsAndRowMenuTargetsItsOwnProject(bool dark, int width)
    {
        using var log = new WorkflowWindowTests.BindingLog(); using var f = new ProjectFixture();
        await f.InitializeAsync(); var a = await f.AddAsync(); var b = await f.AddAsync();
        await ProjectFixture.WriteAsync(a); await ProjectFixture.WriteAsync(b); await f.AcceptAsync();
        f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        await f.Model.SetProjectIncludedAsync(b.Id, false);
        using var maps = new MapSigningFixture();
        var pathA = Path.Combine(Path.GetDirectoryName(a.NativePath)!, "Карта.pdf");
        var pathB = Path.Combine(Path.GetDirectoryName(b.NativePath)!, "Карта.pdf");
        File.Copy(maps.Create("a.pdf", native: Path.GetFileName(a.NativePath)), pathA);
        File.Copy(maps.Create("b.pdf", native: Path.GetFileName(b.NativePath)), pathB);
        var untouched = await File.ReadAllBytesAsync(pathB, Token);
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Assert.Null(f.Model.Error);
            var geometry = Geometry(window);
            var all = window.FindControl<JournalButton>("SignWorkflowMaps")!;
            Assert.True(all.IsEnabled); InWindow(all, window);
            all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => f.Model.HasMapSigningResults && !f.Model.IsBusy);
            Assert.Equal(MapSigningStatus.Signed, Assert.Single(f.Model.MapSigningResults).Status);
            Assert.Equal(untouched, await File.ReadAllBytesAsync(pathB, Token));
            f.Model.SelectedProject = f.Model.Projects.Single(p => p.Project.Id == a.Id);
            var row = f.Model.Projects.Single(p => p.Project.Id == b.Id); var grid = window.FindControl<DataGrid>("WorkflowProjectsGrid")!;
            grid.ScrollIntoView(row, null); Pump();
            var button = grid.GetVisualDescendants().OfType<Button>().Single(x => ReferenceEquals(x.DataContext, row) && x.Flyout is MenuFlyout);
            var menu = (MenuFlyout)button.Flyout!; menu.ShowAt(button); Pump();
            var action = menu.Items.OfType<MenuItem>().Single(x => Equals(x.Header, "Подписать карты раскроя"));
            Assert.True(action.IsEnabled); action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); menu.Hide();
            await Until(() => !f.Model.IsBusy && f.Model.MapSigningResults.Any(r => r.ProjectId == b.Id));
            Assert.Equal(MapSigningStatus.Signed, Assert.Single(f.Model.MapSigningResults).Status);
            Assert.Equal(a.Id, f.Model.SelectedProject!.Project.Id);
            using (var pdf = UglyToad.PdfPig.PdfDocument.Open(pathB)) Assert.Contains("БЛЦБ-009337", pdf.GetPage(1).Text);
            var result = window.FindControl<Button>("WorkflowMapSigningDetails")!; Assert.True(result.IsVisible);
            Assert.False(result.Flyout!.IsOpen); Assert.Equal(geometry, Geometry(window));
            Pump(); Frame(window, $"map-signing-{width}-{dark}");
            InWindow(all, window); InWindow(window.FindControl<JournalButton>("CopyWorkflowCuttingTask")!, window);
            using (File.Open(pathA, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                all.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => !f.Model.IsBusy && f.Model.HasMapSigningProblems);
            }
            Assert.False(result.Flyout.IsOpen); Assert.True(result.IsVisible); Pump(); Assert.Equal(geometry, Geometry(window));
            InWindow(window.FindControl<JournalButton>("CopyWorkflowCuttingTask")!, window);
            Frame(window, $"map-signing-problem-{width}-{dark}");
            result.Flyout.ShowAt(result); Pump(); Assert.True(result.Flyout.IsOpen); Assert.Equal(geometry, Geometry(window));
            Frame(window, $"map-signing-details-{width}-{dark}"); result.Flyout.Hide();
            foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"Horizontal overflow: {scroll.Extent}/{scroll.Viewport}");
            Assert.Empty(log.Errors);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task DirtyOrChangedOrderDoesNotSignAndDoesNotDiscardInput()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        using var maps = new MapSigningFixture(); var map = Path.Combine(Path.GetDirectoryName(project.NativePath)!, "Карта.pdf");
        File.Copy(maps.Create(native: Path.GetFileName(project.NativePath)), map); var original = await File.ReadAllBytesAsync(map, Token);
        f.Model.SelectedDraft!.Coefficient = "1,7";
        await f.Model.SignMapsAsync(token: Token); Assert.Contains("Сохраните", Assert.Single(f.Model.MapSigningResults).Message); Assert.Null(f.Model.Error);
        Assert.Equal("1,7", f.Model.SelectedDraft.Coefficient); Assert.Equal(original, await File.ReadAllBytesAsync(map, Token));
        f.Model.SelectedDraft.Coefficient = "1";
        var book = await f.Store.ReadAsync(Token); var order = Assert.Single(book.Orders);
        await f.Store.WriteOrderAsync(order.Id, order.Version, b => b with { Orders = [order with { Version = order.Version + 1, Blcb = "changed" }] }, Token);
        await f.Model.SignMapsAsync(token: Token); Assert.Contains("другим пользователем", Assert.Single(f.Model.MapSigningResults).Message); Assert.Null(f.Model.Error);
        Assert.Equal(original, await File.ReadAllBytesAsync(map, Token));
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static Rect[] Geometry(Window window) => new[] { "WorkflowProjectsGrid", "WorkflowProjectWorkspace", "WorkflowOrderTotal", "WorkflowStatusBar" }
        .Select(name => window.FindControl<Control>(name)!).Select(c => new Rect(c.TranslatePoint(default, window)!.Value, c.Bounds.Size)).ToArray();
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 500; i++) { Pump(); if (condition()) return; await Task.Delay(10, Token); } Assert.True(condition(), "UI did not settle."); }
    private static void InWindow(Control control, Window window)
    {
        var p = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(p.X, 0, window.ClientSize.Width - control.Bounds.Width + 1);
        Assert.InRange(p.Y, 0, window.ClientSize.Height - control.Bounds.Height + 1);
    }
    private static void Frame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WORKFLOW_MAP_SIGNING_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output);
        window.CaptureRenderedFrame()!.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
