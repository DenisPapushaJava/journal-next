using Jornal.Workflow.Core;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowSettingsTests
{
    [Fact]
    public async Task ProjectFolderSaveKeepsCatalogSavedProjectsAndUnsavedCalculationIntact()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var draft = f.Model.SelectedDraft!; draft.Coefficient = "1,3"; draft.Comment = "Не потерять ввод";
        var quote = draft.Quote; var settings = f.Model.CreateGeneralSettings(); settings.SelectedSectionIndex = 1;
        var root = Path.Combine(f.DirectoryPath, "new-projects"); settings.ProjectRoot = root;
        await settings.SaveCommand.ExecuteAsync(null);
        Assert.Null(settings.Error); Assert.False(settings.HasChanges);
        var after = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(root, after.ProjectRoot); Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(before.Machines, after.Machines); Assert.Equal(before.Rates, after.Rates);
        Assert.Equal(before.Orders[0].Version, after.Orders[0].Version);
        Assert.Equal(before.Orders[0].Specifications[0].Current, after.Orders[0].Specifications[0].Current);
        Assert.Equal(project.NativePath, after.Orders[0].Projects[0].NativePath);
        Assert.True(File.Exists(project.NativePath)); Assert.False(Directory.Exists(root));
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal(quote, draft.Quote);
        Assert.Equal("Не потерять ввод", draft.Comment); Assert.True(f.Model.HasPendingEdits);
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        Assert.Equal(1436.5m, f.Model.SelectedDraft!.Quote!.TotalCost);
    }

    [Fact]
    public async Task UnrelatedOrderWriteDoesNotBlockFolderSaveAndFolderConflictsPreserveInput()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var first = f.Model.CreateGeneralSettings(); var second = f.Model.CreateGeneralSettings(); first.SelectedSectionIndex = second.SelectedSectionIndex = 1;
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        await f.Store.WriteAsync(book.Version, w => WorkflowChanges.CreateOrder(w, "Другой заказчик", w.Employees[0].Id,
            w.Employees[1].Id, w.Employees[1].Id, DateTimeOffset.UtcNow, out _), TestContext.Current.CancellationToken);
        var rootA = Path.Combine(f.DirectoryPath, "a"); var rootB = Path.Combine(f.DirectoryPath, "b");
        first.ProjectRoot = rootA; await first.SaveCommand.ExecuteAsync(null); Assert.Null(first.Error);
        Assert.Equal(2, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Length);
        second.ProjectRoot = rootB; await second.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(second.Error); Assert.True(second.HasChanges); Assert.Equal(rootB, second.ProjectRoot);
        book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); Assert.Equal(rootA, book.ProjectRoot);
        // Repeating the already saved result is a no-op, even when this editor started from an older value.
        second.ProjectRoot = rootA; await second.SaveCommand.ExecuteAsync(null); Assert.Null(second.Error);
        Assert.Equal(book.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
    }

    [Fact]
    public async Task InvalidFolderAndCancelledSettingsDoNotChangeDataAndCatalogCannotResetFolder()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var discarded = f.Model.CreateGeneralSettings(); discarded.ProjectRoot = Path.Combine(f.DirectoryPath, "discarded");
        Assert.Equal(before.ProjectRoot, f.Model.CreateGeneralSettings().ProjectRoot);
        Assert.Equal(before.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
        var settings = f.Model.CreateGeneralSettings(); settings.SelectedSectionIndex = 1; settings.ProjectRoot = "relative-folder";
        await settings.SaveCommand.ExecuteAsync(null); Assert.NotNull(settings.Error); Assert.True(settings.HasChanges);
        Assert.Equal(before.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
        settings.ProjectRoot = Path.Combine(f.DirectoryPath, "saved"); await settings.SaveCommand.ExecuteAsync(null);
        Assert.Null(settings.Error);
        var catalog = f.Model.CreateCatalogSettings(); catalog.SelectedMachine!.Name = "Лазер № 1";
        await catalog.SaveCommand.ExecuteAsync(null); Assert.Null(catalog.Error);
        var after = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(settings.ProjectRoot, after.ProjectRoot);
        Assert.Equal("Лазер № 1", after.Machines[0].Name);
    }
}
