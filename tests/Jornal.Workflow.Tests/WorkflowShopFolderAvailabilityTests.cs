using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Jornal.Workflow.Core;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowShopFolderAvailabilityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [AvaloniaFact]
    public async Task MissingFolderDisablesButtonCopyEnablesItAndF5RestoresOrRemovesAvailability()
    {
        using var f = new ProjectFixture(); await WorkflowShopAutomationWindowTests.ReadyHans(f);
        var row = Assert.Single(f.Model.Projects); Assert.True(row.HasShopFolderPath); Assert.False(row.CanOpenShopFolder);
        await f.Model.RefreshShopFolderAvailabilityAsync(); Assert.False(row.CanOpenShopFolder); Assert.Contains("пока не найдена", row.ShopFolderHint);
        var before = await f.Store.ReadAsync(Token);
        var transfer = await f.Model.PrepareShopTransferAsync(_ => Task.CompletedTask); Assert.True(transfer!.AllPrepared);
        Assert.True(row.CanOpenShopFolder); var folder = row.ShopFolderPath!;
        await f.Model.ReloadCommand.ExecuteAsync(null); row = Assert.Single(f.Model.Projects);
        await Until(() => row.CanOpenShopFolder); Assert.Contains(folder, row.ShopFolderHint);
        f.Model.SelectedDraft!.Coefficient = "1,7";
        Directory.Delete(folder, true); await f.Model.RefreshShopFolderAvailabilityAsync();
        Assert.False(row.CanOpenShopFolder); Assert.Equal("1,7", f.Model.SelectedDraft.Coefficient); Assert.True(f.Model.SelectedDraft.IsDirty);
        Assert.Null(f.Model.Error);
        var after = await f.Store.ReadAsync(Token);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(before.Orders.Single().Version, after.Orders.Single().Version);
        Assert.True(after.Orders.Single().HasCurrentShopHandoff);
    }

    [AvaloniaFact]
    public async Task LateSuccessFromCancelledCheckCannotEnableCurrentButtonAndReadsAreSerialized()
    {
        using var f = new ProjectFixture(); await WorkflowShopAutomationWindowTests.ReadyHans(f);
        await f.Model.RefreshShopFolderAvailabilityAsync(); var row = Assert.Single(f.Model.Projects);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = false; var wronglyEnabled = false;
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(row.CanOpenShopFolder) && row.CanOpenShopFolder) wronglyEnabled = true; };
        var first = f.Model.RefreshShopFolderAvailabilityAsync(async (_, _, _, _) => { started.TrySetResult(); return await release.Task; });
        await started.Task.WaitAsync(Token);
        var second = f.Model.RefreshShopFolderAvailabilityAsync((_, _, _, _) => { secondStarted = true; return Task.FromResult(false); });
        Assert.False(secondStarted); Assert.False(f.Model.IsBusy); // A slow share does not disable editing.
        release.TrySetResult(true); await first; await second;
        Assert.True(secondStarted); Assert.False(wronglyEnabled); Assert.False(row.CanOpenShopFolder);
    }

    [AvaloniaFact]
    public async Task ChangedRootInvalidatesPendingResultAndUnchangedNotificationsDoNotRestartProbe()
    {
        using var f = new ProjectFixture(); await WorkflowShopAutomationWindowTests.ReadyHans(f); await f.Model.RefreshShopFolderAvailabilityAsync();
        var root = f.Model.ShopRoot!; var row = Assert.Single(f.Model.Projects);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); CancellationToken requestToken = default;
        var check = f.Model.RefreshShopFolderAvailabilityAsync(async (_, _, _, token) => { requestToken = token; started.TrySetResult(); return await release.Task; });
        await started.Task.WaitAsync(Token);
        await f.Model.SaveShopRootAsync(root, root, Token); Assert.False(requestToken.IsCancellationRequested);
        var nextRoot = Path.Combine(f.DirectoryPath, "another-shop"); await f.Model.SaveShopRootAsync(root, nextRoot, Token);
        Assert.True(requestToken.IsCancellationRequested); Assert.StartsWith(nextRoot, row.ShopFolderPath);
        release.TrySetResult(true); await check; await f.Model.RefreshShopFolderAvailabilityAsync();
        Assert.False(row.CanOpenShopFolder); Assert.False(Directory.Exists(nextRoot)); Assert.Null(f.Model.Error);
    }

    [AvaloniaFact]
    public async Task UnavailableOrForeignFolderDisablesWithoutAnErrorDialogAndOwnEditedFolderCanBeOpened()
    {
        using var f = new ProjectFixture(); await WorkflowShopAutomationWindowTests.ReadyHans(f);
        var row = Assert.Single(f.Model.Projects);
        await f.Model.RefreshShopFolderAvailabilityAsync((_, _, _, _) => throw new IOException("share unavailable"));
        Assert.False(row.CanOpenShopFolder); Assert.Contains("недоступна", row.ShopFolderHint); Assert.Null(f.Model.Error);
        var transfer = await f.Model.PrepareShopTransferAsync(_ => Task.CompletedTask); Assert.True(transfer!.AllPrepared); Assert.True(row.CanOpenShopFolder);
        var path = row.ShopFolderPath!; await File.WriteAllTextAsync(Path.Combine(path, "operator-note.txt"), "keep", Token);
        await f.Model.RefreshShopFolderAvailabilityAsync(); Assert.True(row.CanOpenShopFolder);
        Assert.Equal(path, await f.Model.ShopFolderToOpenAsync(row, Token));
        var receipt = Path.Combine(path, ".journal-next-task.json");
        await File.WriteAllTextAsync(receipt, "{}", Token); await f.Model.RefreshShopFolderAvailabilityAsync();
        Assert.False(row.CanOpenShopFolder); Assert.Null(f.Model.Error); Assert.True(File.Exists(Path.Combine(path, "operator-note.txt")));
    }

    [AvaloniaFact]
    public async Task DetachCancelsCheckAndLateResultDoesNotChangeRows()
    {
        using var f = new ProjectFixture(); await WorkflowShopAutomationWindowTests.ReadyHans(f); await f.Model.RefreshShopFolderAvailabilityAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); var row = Assert.Single(f.Model.Projects);
        var check = f.Model.RefreshShopFolderAvailabilityAsync(async (_, _, _, _) => { started.TrySetResult(); return await release.Task; });
        await started.Task.WaitAsync(Token); f.Model.Detach(); f.Model.Detach(); release.TrySetResult(true); await check;
        Assert.False(row.CanOpenShopFolder);
    }

    private static async Task Until(Func<bool> ready)
    { for (var i = 0; i < 500 && !ready(); i++) { await Task.Delay(10, Token); Dispatcher.UIThread.RunJobs(); } Assert.True(ready()); }
}
