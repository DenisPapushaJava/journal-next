using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowCuttingTaskFlowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task TaskIsReadOnlyAndUnsavedChangesNeverReachClipboard()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await f.AcceptAsync();
        var copies = new List<string>();
        await f.Model.CopyCuttingTaskAsync(text => { copies.Add(text); return Task.CompletedTask; });
        Assert.Empty(copies); Assert.Contains("БЛЦБ", f.Model.CuttingTaskNotice); Assert.Equal(CuttingTaskTarget.Header, f.Model.GoToCuttingTaskProblem());
        f.Model.Blcb = "БЛЦБ-009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        var before = JsonSerializer.Serialize(await f.Store.ReadAsync(Token));
        await f.Model.CopyCuttingTaskAsync(text => { copies.Add(text); return Task.CompletedTask; });
        Assert.Single(copies); Assert.StartsWith("9337  Иванов", copies[0]); Assert.Contains("скопировано", f.Model.CuttingTaskNotice);
        Assert.Equal(before, JsonSerializer.Serialize(await f.Store.ReadAsync(Token)));
        f.Model.SelectedDraft!.HasChamfer = true;
        await f.Model.CopyCuttingTaskAsync(text => { copies.Add(text); return Task.CompletedTask; });
        Assert.Single(copies); Assert.Equal(p.Id, f.Model.SelectedDraft.Id); Assert.Contains("Сохраните", f.Model.CuttingTaskNotice);
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null);
        Assert.DoesNotContain("Задание скопировано", f.Model.CuttingTaskNotice);
        await f.Model.CopyCuttingTaskAsync(text => { copies.Add(text); return Task.CompletedTask; }); Assert.Contains("ФАСКА", copies[1]);
    }

    [Fact]
    public async Task MissingProjectAndNewReportHaveExplicitTargets()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var a = await f.AddAsync(); var b = await f.AddAsync();
        await ProjectFixture.WriteAsync(a); await f.AcceptAsync();
        f.Model.Blcb = "9337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        await f.Model.CopyCuttingTaskAsync(_ => throw new InvalidOperationException("Must not copy"));
        Assert.Equal(CuttingTaskTarget.Project, f.Model.GoToCuttingTaskProblem()); Assert.Equal(b.Id, f.Model.SelectedProject!.Project.Id);
        await f.Model.SetProjectIncludedAsync(b.Id, false);
        await ProjectFixture.WriteAsync(a, 20000); await f.ReviewAsync();
        await f.Model.CopyCuttingTaskAsync(_ => throw new InvalidOperationException("Must not copy"));
        Assert.Equal(CuttingTaskTarget.Reports, f.Model.GoToCuttingTaskProblem()); Assert.False(f.Model.HasCuttingTaskText);
    }

    [Fact]
    public async Task ClipboardFailureRetriesOnlyTextAndLateCompletionDoesNotAffectAnotherCard()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await f.AcceptAsync();
        f.Model.Blcb = "9337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        var before = await f.Store.ReadAsync(Token);
        await f.Model.CopyCuttingTaskAsync(_ => throw new IOException("Clipboard unavailable"));
        Assert.Contains("Не удалось скопировать", f.Model.CuttingTaskNotice); Assert.True(f.Model.CanCopyCuttingTask);
        await f.Model.CopyCuttingTaskAsync(_ => Task.CompletedTask); Assert.Contains("Задание скопировано", f.Model.CuttingTaskNotice);
        Assert.Equal(before.Version, (await f.Store.ReadAsync(Token)).Version);
        var started = new TaskCompletionSource(); var complete = new TaskCompletionSource();
        var copying = f.Model.CopyCuttingTaskAsync(_ => { started.SetResult(); return complete.Task; }); await started.Task;
        var duplicate = false; await f.Model.CopyCuttingTaskAsync(_ => { duplicate = true; return Task.CompletedTask; }); Assert.False(duplicate);
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Другой"; f.Model.NewCustomer = "Другой заказчик"; await f.Model.CreateOrderCommand.ExecuteAsync(null);
        complete.SetResult(); await copying;
        Assert.False(f.Model.IsCuttingTaskVisible); Assert.DoesNotContain("скопировано", f.Model.CuttingTaskNotice);
    }

    [Fact]
    public async Task BendingFolderIsCreatedOnlyAfterSaveAndUncheckingPreservesFiles()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await f.AcceptAsync();
        var path = Path.Combine(Path.GetDirectoryName(p.NativePath!)!, "ГИБКА");
        f.Model.SelectedDraft!.HasBending = true; Assert.False(Directory.Exists(path));
        f.Model.CancelDraftCommand.Execute(null); Assert.False(Directory.Exists(path));
        f.Model.SelectedDraft!.HasBending = true; await f.Model.SaveSpecificationCommand.ExecuteAsync(null);
        Assert.Null(f.Model.Error); Assert.True(Directory.Exists(path)); Assert.True(f.Model.SelectedDraft!.HasSavedBendingFolder);
        await File.WriteAllTextAsync(Path.Combine(path, "гибка.txt"), "Сохранить", Token);
        // Editing a price/coefficient does not probe the project share again.
        File.Delete(p.NativePath!);
        f.Model.SelectedDraft!.Coefficient = "1,2"; await f.Model.SaveSpecificationCommand.ExecuteAsync(null);
        Assert.Null(f.Model.Error); Assert.False(f.Model.HasBendingFolderProblem);
        await File.WriteAllTextAsync(p.NativePath!, "", Token);
        var before = await f.Store.ReadAsync(Token);
        Assert.Equal(path, await f.Model.PrepareSelectedBendingFolderAsync(Token));
        Assert.Equal(before.Version, (await f.Store.ReadAsync(Token)).Version);
        f.Model.SelectedDraft.HasBending = false; await f.Model.SaveSpecificationCommand.ExecuteAsync(null);
        Assert.Equal("Сохранить", await File.ReadAllTextAsync(Path.Combine(path, "гибка.txt"), Token)); Assert.False(f.Model.SelectedDraft!.HasSavedBendingFolder);
    }

    [Fact]
    public async Task FolderFailureKeepsSavedCalculationAndRetryDoesNotWriteDatabase()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p);
        var path = Path.Combine(Path.GetDirectoryName(p.NativePath!)!, "ГИБКА"); await File.WriteAllTextAsync(path, "Препятствие", Token);
        var review = await f.ReviewAsync(); review.Selected!.Draft!.HasBending = true; await review.SaveCommand.ExecuteAsync(null);
        Assert.True(review.WasSaved); Assert.Null(review.Error); Assert.Null(f.Model.Error); Assert.True(f.Model.HasBendingFolderProblem);
        var saved = await f.Store.ReadAsync(Token); Assert.True(saved.Orders.Single().Specifications.Single().Current.Parameters.HasBending);
        Assert.False(f.Model.SelectedDraft!.IsDirty);
        Assert.Equal("Препятствие", await File.ReadAllTextAsync(path, Token)); File.Delete(path);
        await f.Model.RetryBendingFoldersCommand.ExecuteAsync(null);
        Assert.False(f.Model.HasBendingFolderProblem); Assert.True(Directory.Exists(path));
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(await f.Store.ReadAsync(Token)));
    }

    [Fact]
    public async Task SaveConflictNeverCreatesBendingFolder()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await f.AcceptAsync();
        f.Model.SelectedDraft!.HasBending = true;
        var before = await f.Store.ReadAsync(Token); var order = before.Orders.Single();
        await f.Store.WriteOrderAsync(order.Id, order.Version, b => WorkflowChanges.UpdateOrderFromManagerName(b, order.Id, order.Version,
            "Другой менеджер", order.TechnologistId, "123", order.TechnologistId, DateTimeOffset.UtcNow), Token);
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null);
        Assert.NotNull(f.Model.Error); Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(p.NativePath!)!, "ГИБКА")));
    }

    [Fact]
    public async Task MissingProjectFolderIsNotRecreatedAndDirectoryLinksAreRejected()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        var folder = Path.GetDirectoryName(p.NativePath!)!; Directory.Delete(folder, true);
        await Assert.ThrowsAnyAsync<IOException>(() => WorkflowBendingFiles.EnsureAsync(p, Token)); Assert.False(Directory.Exists(folder));
        Directory.CreateDirectory(folder); await File.WriteAllTextAsync(p.NativePath!, "", Token);
        var destination = Path.Combine(f.DirectoryPath, "elsewhere"); Directory.CreateDirectory(destination);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(folder, "ГИБКА"), destination);
            await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowBendingFiles.EnsureAsync(p, Token)); Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkflowBendingFiles.EnsureAsync(p, new CancellationToken(true)));
    }
}
