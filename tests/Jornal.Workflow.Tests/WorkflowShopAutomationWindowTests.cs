using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Automation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowShopAutomationWindowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    internal static async Task<CuttingProject> ReadyHans(ProjectFixture f)
    {
        await f.InitializeAsync(); var project = await f.AddAsync("HansLaser");
        var book = await f.Store.ReadAsync(Token); var order = Assert.Single(book.Orders);
        var parameters = new CuttingParameters("HansLaser", "Ст3", "Базовая", 6, "Лист", 1, 1, 1, null, null, 1, false, null) { MachineId = project.MachineId };
        await f.Store.WriteOrderAsync(order.Id, order.Version, b => WorkflowChanges.SaveSpecification(b, order.Id, order.Version, project.Id,
            parameters, "1026-101507", new(2026, 10, 26), book.Employees[1].Id, DateTimeOffset.Now, profile: project.ImportProfile), Token);
        await f.Model.ReloadCommand.ExecuteAsync(null); f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        Assert.Null(f.Model.Error); await f.Model.SaveShopRootAsync(null, Path.Combine(f.DirectoryPath, "shop"), Token);
        var folder = Path.GetDirectoryName(project.NativePath!)!;
        await File.WriteAllTextAsync(Path.Combine(folder, "Произвольное имя.GNC"), "UP bytes", Token);
        await File.WriteAllTextAsync(Path.Combine(folder, "Карта с незаполненными полями.DOC"), "DOC bytes", Token);
        await File.WriteAllTextAsync(Path.Combine(folder, "Дополнительная.Gnf.DOC"), "another DOC", Token);
        return project;
    }

    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task OneClickPreparesHansCopiesTextAndRepeatDoesNotRewriteFiles(bool dark, int width)
    {
        using var log = new WorkflowWindowTests.BindingLog(); using var f = new ProjectFixture(); var project = await ReadyHans(f);
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Update(p => p with { DarkTheme = dark });
        var window = new RecordingWindow(preferences) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);
            var workspace = window.FindControl<Grid>("JournalWorkspace")!;
            var beforeTransfer = window.FindControl<DataGrid>("WorkflowProjectsGrid")!.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == "Открыть папку задания в цехе");
            Assert.False(beforeTransfer.IsEffectivelyEnabled);
            Assert.True(beforeTransfer.GetValue(ToolTip.ShowOnDisabledProperty));
            Assert.True(window.FindControl<DataGrid>("WorkflowProjectsGrid")!.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == "Открыть папку проекта").IsEffectivelyEnabled);
            Frame(window, $"shop-folder-disabled-{width}-{dark}");
            Click(window.FindControl<Button>("CopyWorkflowCuttingTask")!);
            await Until(() => !f.Model.IsBusy); var model = Assert.IsType<WorkflowShopTransferViewModel>(f.Model.ShopTransfer);
            Assert.True(model.AllPrepared); Assert.True(model.TextCopied); Assert.Null(model.Error); Assert.Empty(window.OwnedWindows); Assert.Equal(1, window.CopyCount);
            Assert.Contains("9337", window.Copied); var row = Assert.Single(model.Projects);
            Assert.True(File.Exists(Path.Combine(row.Destination, "Дополнительная.Gnf.DOC"))); Assert.Equal(project.Name, Path.GetFileName(row.Destination));
            var file = Path.Combine(row.Destination, "Произвольное имя.GNC"); var before = File.GetLastWriteTimeUtc(file);
            var shopButton = window.FindControl<DataGrid>("WorkflowProjectsGrid")!.GetVisualDescendants().OfType<Button>()
                .Single(b => AutomationProperties.GetName(b) == "Открыть папку задания в цехе");
            Assert.True(shopButton.IsEffectivelyEnabled); Assert.Equal(row.Destination, f.Model.Projects.Single().ShopFolderPath);
            Assert.Contains(row.Destination, f.Model.Projects.Single().ShopFolderHint); Click(shopButton);
            await Until(() => window.OpenedFolder is not null);
            Assert.Equal(row.Destination, window.OpenedFolder); Assert.NotEqual(Path.GetDirectoryName(project.NativePath!), window.OpenedFolder);
            Assert.Equal(1, window.CopyCount); Assert.Equal(before, File.GetLastWriteTimeUtc(file));
            InWindow(window.FindControl<Grid>("WorkflowShopTransferNotice")!, window);
            Frame(window, $"shop-auto-success-{width}-{dark}");
            Click(window.FindControl<Button>("CopyWorkflowCuttingTask")!); await Until(() => !f.Model.IsBusy);
            Assert.Equal(2, window.CopyCount); Assert.Empty(window.OwnedWindows); Assert.Equal(before, File.GetLastWriteTimeUtc(file));
            f.Model.Blcb = "unsaved"; Assert.False(f.Model.HasShopTransfer); f.Model.Blcb = "009337"; Assert.True(f.Model.HasShopTransfer); Pump();
            Click(window.FindControl<Button>("WorkflowShopTransferDetails")!); await Until(() => window.OwnedWindows.Count == 1);
            var details = Assert.IsType<WorkflowShopTransferWindow>(window.OwnedWindows.Single());
            Assert.Same(f.Model.ShopTransfer, details.DataContext); Assert.True(((WorkflowShopTransferViewModel)details.DataContext!).AllPrepared);
            Assert.Equal(project, row.Plan.Project);
            Frame(details, $"shop-auto-details-{width}-{dark}"); details.Close();
            Assert.Empty(log.Errors);
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ClipboardFailureOffersTextOnlyRetryAndChangedSentFilesOpenReviewWithoutWriting()
    {
        using var f = new ProjectFixture(); var project = await ReadyHans(f);
        var window = new RecordingWindow(new(Path.Combine(f.DirectoryPath, "preferences.json"))) { DataContext = f.Model, FailCopy = true };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Click(window.FindControl<Button>("CopyWorkflowCuttingTask")!); await Until(() => !f.Model.IsBusy);
            var prepared = f.Model.ShopTransfer!; Assert.True(prepared.AllPrepared); Assert.True(prepared.NeedsClipboardRetry); Assert.Empty(window.OwnedWindows);
            var row = Assert.Single(prepared.Projects); var path = Path.Combine(row.Destination, "Произвольное имя.GNC"); var before = File.GetLastWriteTimeUtc(path);
            var retry = window.FindControl<Button>("RetryWorkflowShopTransferText")!; Assert.True(retry.IsEffectivelyVisible);
            window.FailCopy = false; Click(retry); await Until(() => !f.Model.IsBusy);
            Assert.Equal(1, window.CopyCount); Assert.False(prepared.NeedsClipboardRetry); Assert.Equal(before, File.GetLastWriteTimeUtc(path));
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(project.NativePath!)!, "Произвольное имя.GNC"), "updated UP", Token);
            Click(window.FindControl<Button>("CopyWorkflowCuttingTask")!); await Until(() => window.OwnedWindows.Count == 1);
            var review = f.Model.ShopTransfer!; Assert.False(review.CanStartAutomatically); Assert.True(review.ShowReplacement); Assert.False(review.CanCopy);
            Assert.Equal("UP bytes", await File.ReadAllTextAsync(path, Token)); Assert.Equal(1, window.CopyCount);
            Frame(Assert.IsType<WorkflowShopTransferWindow>(window.OwnedWindows.Single()), "shop-auto-replacement-review");
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task BusyTransferBlocksSecondClickClosingAndRestartAndResetDropsOldNotice()
    {
        using var f = new ProjectFixture(); await ReadyHans(f);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new RecordingWindow(new(Path.Combine(f.DirectoryPath, "preferences.json"))) { DataContext = f.Model, ClipboardRelease = release };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Click(window.FindControl<Button>("CopyWorkflowCuttingTask")!);
            await Until(() => window.ClipboardStarted); Assert.True(f.Model.IsBusy);
            Assert.False(window.FindControl<Grid>("JournalWorkspace")!.IsEffectivelyEnabled);
            Assert.False(f.Model.ShopTransfer!.CanCancel);
            Assert.Null(await f.Model.PrepareShopTransferAsync(_ => Task.CompletedTask));
            window.Close(); Assert.True(window.IsVisible); Assert.False(window.FindControl<Button>("CopyWorkflowCuttingTask")!.IsEffectivelyEnabled);
            release.SetResult(); await Until(() => !f.Model.IsBusy); Assert.Equal(1, window.CopyCount);
            await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Null(f.Model.ShopTransfer);
            Assert.False(window.FindControl<Grid>("WorkflowShopTransferNotice")!.IsEffectivelyVisible);
        }
        finally { release.TrySetResult(); await Until(() => !f.Model.IsBusy); window.Close(); }
    }

    [AvaloniaFact]
    public async Task ExplicitReviewShowsConfidentSelectionWithoutCopyingAndCanCancelScan()
    {
        using var f = new ProjectFixture(); await ReadyHans(f);
        var model = await f.Model.PrepareShopTransferAsync(_ => throw new InvalidOperationException("Must not copy"), reviewOnly: true);
        Assert.NotNull(model); Assert.True(model.CanStartAutomatically); Assert.False(Directory.Exists(f.Model.ShopRoot));
        var scans = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var files = new ShopFixture("HansLaser");
        var pending = new WorkflowShopTransferViewModel(files.ShopRoot, "db", files.Order, files.Book, async token =>
        { scans.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }, _ => throw new InvalidOperationException("Must not copy"));
        var scan = pending.RefreshAsync(); await scans.Task.WaitAsync(Token); pending.Cancel(); await scan;
        Assert.True(pending.WasCancelled); Assert.False(pending.CanStartAutomatically); Assert.False(Directory.Exists(files.ShopRoot));
    }

    private sealed class RecordingWindow(WorkflowPreferencesStore preferences) : WorkflowWindow(preferences)
    {
        public string? Copied { get; private set; }
        public int CopyCount { get; private set; }
        public bool FailCopy { get; set; }
        public bool ClipboardStarted { get; private set; }
        public string? OpenedFolder { get; private set; }
        public TaskCompletionSource? ClipboardRelease { get; init; }
        protected override async Task WriteCuttingTaskClipboardAsync(string text)
        {
            ClipboardStarted = true; if (ClipboardRelease is not null) await ClipboardRelease.Task;
            if (FailCopy) throw new IOException("test"); Copied = text; CopyCount++;
        }
        protected override Task<bool> OpenFolderAsync(string path) { OpenedFolder = path; return Task.FromResult(Directory.Exists(path)); }
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(done()); }
    private static void Click(Control control)
    {
        Pump(); var window = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void InWindow(Control control, Window window)
    {
        var p = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(p.X, 0, window.ClientSize.Width); Assert.InRange(p.X + control.Bounds.Width, 0, window.ClientSize.Width);
        Assert.InRange(p.Y, 0, window.ClientSize.Height); Assert.InRange(p.Y + control.Bounds.Height, 0, window.ClientSize.Height);
    }
    private static void Frame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
