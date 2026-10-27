using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowReviewFixTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaTheory]
    [InlineData("schema")] [InlineData("rows")] [InlineData("size")] [InlineData("json")]
    public async Task InvalidRestartDraftDoesNotPreventRepeatedStartupOrReplaceSavedData(string kind)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var path = Path.Combine(f.DirectoryPath, "update-restart.json");
        var text = kind switch
        {
            "schema" => "{\"Schema\":999,\"ResultText\":\"Сохранить черновик\"}",
            "rows" => "{\"Schema\":1,\"ResultText\":\"Сохранить черновик\",\"Results\":[{\"OrderId\":\"00000000-0000-0000-0000-000000000000\",\"Version\":0,\"Text\":\"текст\"}]}",
            "size" => new string('x', 2 * 1024 * 1024 + 1),
            _ => "{\"Schema\":1,"
        };
        await File.WriteAllTextAsync(path, text, Token);
        var draftBytes = await File.ReadAllBytesAsync(path, Token);
        var workspaceBytes = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
        var orderId = Assert.Single((await f.Store.ReadAsync(Token)).Orders).Id;
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var model = new WorkflowViewModel(f.Store, new AppUpdatesViewModel(new WorkflowAppUpdater()), preferences: preferences);
            var window = new WorkflowWindow(preferences) { DataContext = model };
            try
            {
                window.Show(); await Until(() => model.CanUse && model.Error is not null);
                Assert.Contains("Не удалось восстановить результат", model.Error);
                Assert.Equal(orderId, Assert.Single(model.Orders).Order.Id);
                Assert.NotNull(model.CurrentEmployee); Assert.True(window.IsVisible);
                Assert.Equal(draftBytes, await File.ReadAllBytesAsync(path, Token));
                Assert.Equal(workspaceBytes, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
                model.NewOrderCommand.Execute(null); Assert.True(model.IsCreating);
            }
            finally { window.Close(); }
        }
    }

    [Theory]
    [InlineData("Лазер")] [InlineData("HansLaser")] [InlineData("ГАР")]
    public async Task UnreadableCandidatePreservesOtherFilesAndRequiresManualExclusion(string machine)
    {
        using var f = new ShopFixture(machine);
        var path = Path.Combine(f.Source, machine == "HansLaser" ? "Недоступная карта.DOC" : "Недоступный документ.pdf");
        await File.WriteAllTextAsync(path, "other document", Token);
        using (var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var plan = Assert.Single(await f.Discover()); Assert.Null(plan.Problem); Assert.Equal(3, plan.Files.Length);
            Assert.True(plan.NeedsReview); Assert.Contains("недоступные", plan.ReviewReason);
            var row = new WorkflowShopProjectChoice(plan, null, null);
            var unavailable = Assert.Single(row.Files, file => !file.CanSelect);
            Assert.Equal(Path.GetFileName(path), unavailable.Name); Assert.Contains("Файл недоступен", unavailable.Detail);
            foreach (var file in row.Files.Where(file => file.CanSelect)) file.IsSelected = true;
            Assert.False(row.CanCopy);
            await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Copy(row.Selection));
            row.Reviewed = true; Assert.True(row.CanCopy);
            unavailable.IsSelected = true; row.Reviewed = true; Assert.False(row.CanCopy);
            await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Copy(row.Selection));
            unavailable.IsSelected = false; row.Reviewed = true;
            var copied = Assert.Single(await f.Copy(row.Selection)); Assert.True(copied.Success);
            Assert.False(File.Exists(Path.Combine(plan.Destination, Path.GetFileName(path))));
            Assert.All(plan.Files.Where(file => file.IsReadable), file => Assert.True(File.Exists(Path.Combine(plan.Destination, file.Name))));
            row.ApplyResult(copied);
            Assert.True(Assert.Single(await f.Copy(row.Selection)).Unchanged);
        }
        var refreshed = Assert.Single(await f.Discover());
        Assert.All(refreshed.Files, file => Assert.True(file.IsReadable));
        if (machine == "HansLaser") Assert.False(refreshed.NeedsReview);
    }

    [Fact]
    public async Task ManualExclusionDoesNotSkipHashChecksOfOtherReadableCandidates()
    {
        using var f = new ShopFixture("HansLaser");
        var locked = Path.Combine(f.Source, "Недоступная.DOC");
        var ignored = Path.Combine(f.Source, "Исключённая.DOC");
        await File.WriteAllTextAsync(locked, "locked", Token);
        await File.WriteAllTextAsync(ignored, "original", Token);
        using var lease = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var plan = Assert.Single(await f.Discover());
        var selected = plan.Files.Where(file => file.IsReadable && file.SourcePath != ignored).ToArray();
        await File.WriteAllTextAsync(ignored, "changed!", Token);
        var result = Assert.Single(await f.Copy(new(plan, selected, true, null)));
        Assert.False(result.Success); Assert.Contains("Изменился файл", result.Message);
        Assert.False(Directory.Exists(plan.Destination));
    }

    [Fact]
    public async Task UnreadableBendingFileStillBlocksWholeProject()
    {
        using var f = new ShopFixture("HansLaser", true);
        var path = Path.Combine(f.Source, "ГИБКА", "рисунок.png");
        await File.WriteAllTextAsync(path, "drawing", Token);
        using var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var plan = Assert.Single(await f.Discover());
        Assert.NotNull(plan.Problem); Assert.Empty(plan.Files);
        Assert.False(Directory.Exists(plan.Destination));
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task PreviewShowsUnavailableFileDisabledAndAllowsReviewedRemainder(bool dark)
    {
        using var log = new WorkflowWindowTests.BindingLog();
        using var f = new ShopFixture("HansLaser");
        var path = Path.Combine(f.Source, "Посторонняя заблокированная карта.DOC");
        await File.WriteAllTextAsync(path, "other document", Token);
        using var lease = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        string? text = null;
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "test-db", f.Order, f.Book, _ => Task.CompletedTask,
            value => { text = value; return Task.CompletedTask; });
        var window = new WorkflowShopTransferWindow { DataContext = model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = 800, Height = 540 };
        try
        {
            window.Show(); await Until(() => !model.IsBusy); Assert.Null(model.Error);
            Assert.False(model.CanStartAutomatically); Assert.False(model.CanCopy);
            var row = Assert.Single(model.Projects);
            var unavailable = Assert.Single(row.Files, file => !file.CanSelect);
            var controls = window.GetVisualDescendants().OfType<Control>().Where(control => ReferenceEquals(control.DataContext, unavailable)).ToArray();
            Assert.False(Assert.Single(controls.OfType<CheckBox>()).IsEffectivelyEnabled);
            Assert.False(Assert.Single(controls.OfType<Button>(), button => Equals(button.Content, "Открыть")).IsEffectivelyEnabled);
            Assert.True(window.FindControl<CheckBox>("ShopReviewed")!.IsEffectivelyVisible);
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is { } output)
            {
                Directory.CreateDirectory(output);
                window.CaptureRenderedFrame()!.Save(Path.Combine(output, $"unavailable-candidate-{dark}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            row.Reviewed = true; Assert.True(model.CanCopy);
            await model.CopyAsync(); Assert.True(model.AllPrepared); Assert.True(model.TextCopied); Assert.NotEmpty(text!);
            Assert.False(File.Exists(Path.Combine(row.Destination, Path.GetFileName(path))));
            Assert.Empty(log.Errors);
        }
        finally { window.Close(); }
    }

    private static async Task Until(Func<bool> ready)
    {
        for (var i = 0; i < 500 && !ready(); i++) { await Task.Delay(10, Token); Dispatcher.UIThread.RunJobs(); }
        Dispatcher.UIThread.RunJobs(); Assert.True(ready());
    }
}
