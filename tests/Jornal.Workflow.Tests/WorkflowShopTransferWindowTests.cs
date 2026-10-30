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

public sealed class WorkflowShopTransferWindowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [AvaloniaTheory]
    [InlineData(false, 800, 540)] [InlineData(true, 800, 540)]
    [InlineData(false, 1040, 650)] [InlineData(true, 1040, 650)]
    public async Task CompactPreviewChecksManualFilesCopiesWholeBendingAndFitsBothThemes(bool dark, int width, int height)
    {
        using var log = new WorkflowWindowTests.BindingLog(); using var f = new ShopFixture("Плазма", true);
        string? copied = null; var failClipboard = true;
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "test-db", f.Order, f.Book, _ => Task.CompletedTask,
            text => { if (failClipboard) throw new IOException("test"); copied = text; return Task.CompletedTask; });
        var window = new WorkflowShopTransferWindow { DataContext = model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = width, Height = height };
        try
        {
            window.Show(); await Until(() => !model.IsBusy); Assert.Null(model.Error);
            Assert.False(model.CanCopy); Assert.False(Directory.Exists(f.ShopRoot));
            var project = Assert.Single(model.Projects); Assert.Equal(project.Destination, window.FindControl<TextBox>("ShopDestination")!.Text);
            Assert.Contains("не подтверждены автоматически", project.SelectionHelp);
            foreach (var file in project.Files) file.IsSelected = true;
            Assert.False(model.CanCopy); project.Reviewed = true; Assert.True(model.CanCopy);
            Pump(); Frame(window, $"shop-preview-{width}-{dark}");
            InWindow(window.FindControl<Button>("ShopCopy")!, window); InWindow(window.FindControl<Button>("ShopClose")!, window);
            InWindow(window.FindControl<CheckBox>("ShopReviewed")!, window); InWindow(window.FindControl<TextBox>("ShopDestination")!, window);
            foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"Horizontal overflow: {scroll.Extent}/{scroll.Viewport}");
            await model.CopyAsync(); Pump();
            Assert.True(model.AllPrepared); Assert.Null(copied); Assert.Contains("Файлы подготовлены", model.Error);
            Assert.True(Directory.Exists(Path.Combine(project.Destination, "ГИБКА"))); Assert.True(model.CanCopyText);
            failClipboard = false; await model.CopyTextCommand.ExecuteAsync(null);
            Assert.Null(model.Error); Assert.NotEmpty(copied!); Assert.Contains("Текст задания скопирован", model.Status);
            Frame(window, $"shop-complete-{width}-{dark}");
            Assert.Empty(log.Errors);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ParentGuardsDirtyInputsAndRemoteOrderChangeBeforeCopy()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        await f.Model.SaveShopRootAsync(null, Path.Combine(f.DirectoryPath, "shop"), Token);
        f.Model.Blcb = "changed"; Assert.Null(f.Model.CreateShopTransfer(_ => Task.CompletedTask));
        f.Model.Blcb = "009337";
        var preview = Assert.IsType<WorkflowShopTransferViewModel>(f.Model.CreateShopTransfer(_ => Task.CompletedTask));
        await preview.RefreshAsync(); Assert.Single(preview.Projects);
        var before = await f.Store.ReadAsync(Token); var order = Assert.Single(before.Orders);
        await f.Store.WriteOrderAsync(order.Id, order.Version, b => b with { Orders = [order with { Version = order.Version + 1 }] }, Token);
        await preview.RefreshAsync(); Assert.Contains("другим пользователем", preview.Error);
        Assert.False(Directory.Exists(f.Model.ShopRoot)); Assert.Equal("009337", f.Model.Blcb);
    }

    [AvaloniaFact]
    public async Task MainActionOpensPreviewAndPreparesFilesBeforeCopyingText()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        await f.Model.SaveShopRootAsync(null, Path.Combine(f.DirectoryPath, "shop"), Token);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(project.NativePath!)!, "Лист.cnc"), "UP", Token);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(project.NativePath!)!, "Карта.pdf"), "Map", Token);
        f.Model.CuttingTaskText += "Проверить кромку перед резкой";
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var window = new RecordingWindow(preferences) { DataContext = f.Model };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var button = window.FindControl<Button>("CopyWorkflowCuttingTask")!;
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
            await Until(() => window.OwnedWindows.OfType<WorkflowShopTransferWindow>().Any());
            var dialog = Assert.Single(window.OwnedWindows.OfType<WorkflowShopTransferWindow>());
            var model = Assert.IsType<WorkflowShopTransferViewModel>(dialog.DataContext); await Until(() => !model.IsBusy);
            Assert.Null(window.Copied); Assert.False(Directory.Exists(f.Model.ShopRoot));
            var row = Assert.Single(model.Projects); foreach (var file in row.Files) file.IsSelected = true; row.Reviewed = true;
            await model.CopyAsync(); Assert.True(model.AllPrepared); Assert.NotEmpty(window.Copied!); Assert.Null(model.Error);
            Assert.EndsWith("Проверить кромку перед резкой", window.Copied);
            Assert.True(File.Exists(Path.Combine(row.Destination, "Лист.cnc"))); dialog.Close();
            Assert.Contains("Задание скопировано", f.Model.CuttingTaskNotice);
            var toggle = window.FindControl<Button>("ToggleWorkflowCuttingTaskPreview")!;
            Assert.True(toggle.IsEffectivelyVisible); toggle.Flyout!.ShowAt(toggle); Pump();
            Assert.Equal(window.Copied, window.FindControl<TextBox>("WorkflowCuttingTaskPreview")!.Text);
            var before = await f.Store.ReadAsync(Token); var stamp = File.GetLastWriteTimeUtc(Path.Combine(row.Destination, "Лист.cnc"));
            window.FindControl<Button>("CopyWorkflowTaskPreviewText")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Until(() => !f.Model.IsCopyingCuttingTask);
            Assert.Same(model, f.Model.ShopTransfer); Assert.True(model.AllPrepared);
            Assert.Equal(before.Version, (await f.Store.ReadAsync(Token)).Version);
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(row.Destination, "Лист.cnc")));
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); }
    }

    private sealed class RecordingWindow(WorkflowPreferencesStore preferences) : WorkflowWindow(preferences)
    {
        public string? Copied { get; private set; }
        protected override Task WriteCuttingTaskClipboardAsync(string text) { Copied = text; return Task.CompletedTask; }
    }

    [AvaloniaFact]
    public async Task CancelRequestsStopButKeepsWindowUntilOperationFinishes()
    {
        using var f = new ShopFixture("HansLaser"); var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "test-db", f.Order, f.Book, async token =>
        { reading.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }, _ => Task.CompletedTask);
        var window = new WorkflowShopTransferWindow { DataContext = model };
        window.Show(); await reading.Task.WaitAsync(Token); Assert.True(model.IsBusy); Assert.Equal("Отменить", model.CloseCaption);
        window.Close(); Assert.True(window.IsVisible); await Until(() => !model.IsBusy);
        Assert.Contains("отменён", model.Status); Assert.False(Directory.Exists(f.ShopRoot)); window.Close(); Assert.False(window.IsVisible);
    }

    [AvaloniaFact]
    public async Task MissingFilesExplainTheNextStepWithoutCreatingDestinationFolders()
    {
        using var f = new ShopFixture("HansLaser"); File.Delete(Path.Combine(f.Source, "Лист.GNC")); File.Delete(Path.Combine(f.Source, "Карта.DOC"));
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "test-db", f.Order, f.Book, _ => Task.CompletedTask, _ => Task.CompletedTask);
        await model.RefreshAsync(); var project = Assert.Single(model.Projects);
        Assert.False(model.CanCopy); Assert.True(project.HasSelectionHelp); Assert.Contains("Обновить подбор", project.SelectionHelp);
        Assert.False(Directory.Exists(f.ShopRoot));
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> ready)
    { for (var i = 0; i < 500 && !ready(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(ready()); }
    private static void InWindow(Control control, Window window)
    {
        var p = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(p.X, 0, window.ClientSize.Width); Assert.InRange(p.Y, 0, window.ClientSize.Height);
        Assert.InRange(p.X + control.Bounds.Width, 0, window.ClientSize.Width); Assert.InRange(p.Y + control.Bounds.Height, 0, window.ClientSize.Height);
    }
    private static void Frame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
