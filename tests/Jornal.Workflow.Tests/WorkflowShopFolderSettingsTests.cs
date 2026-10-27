using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowShopFolderSettingsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("005072")] [InlineData("БЛЦБ-005072")] [InlineData("12345678901234567890")]
    public async Task FullNumberAndEstablishedProjectNameArePreservedWithoutFilesystemAccess(string blcb)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync("Плазма");
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single();
        var name = "60 мм 2026-06-02-101919--ПЛАЗМА";
        order = order with { Blcb = blcb, Projects = [project with { Name = name }] };
        var employees = book.Employees.Select(e => e.Id == order.ManagerId ? e with { Name = "Голубева" } : e).ToArray();
        var customers = book.Customers.Select(c => c.Id == order.CustomerId ? c with { Name = "ФОРММАТ" } : c).ToArray();
        var root = Path.Combine(f.DirectoryPath, "Принтер на складе");
        var plan = Assert.Single(WorkflowShopFolderPaths.Plan(root, order, employees, customers, book.Machines));
        var number = blcb.StartsWith("БЛЦБ-", StringComparison.Ordinal) ? blcb : "БЛЦБ-" + blcb;
        Assert.Equal(Path.Combine(root, "Плазма", "Голубева", number + " ФОРММАТ", name), plan.Path);
        Assert.Equal(name, plan.ProjectName); Assert.Equal(project.Id, plan.ProjectId);
        Assert.Equal(name, Path.GetFileName(plan.Path)); Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task PreviewUsesOnlyCurrentIncludedProjectsAndNeverChangesSourceNames()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single();
        var old = project with { Id = Guid.NewGuid(), Variant = 1, Name = "Старый проект" };
        var excluded = project with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Исключённый" };
        var deleted = project with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), DeletedAt = DateTimeOffset.UtcNow, DeletedBy = book.Employees[1].Id };
        order = order with { Blcb = "000001", Projects = [old, project with { Variant = 2 }, excluded, deleted],
            Items = [new(project.ItemId, 1, project.Id) { ReviewedThroughVariant = 2 }, new(excluded.ItemId, 2, excluded.Id) { IsIncluded = false }, new(deleted.ItemId, 3, deleted.Id)] };
        var employees = book.Employees.Select(e => e.Id == order.ManagerId ? e with { Name = "Менеджер/отдел" } : e).ToArray();
        var plan = Assert.Single(WorkflowShopFolderPaths.Plan(Path.Combine(f.DirectoryPath, "shop"), order, employees, book.Customers, book.Machines));
        Assert.Equal(project.Id, plan.ProjectId); Assert.Contains("Менеджер_отдел", plan.Path);
        Assert.Equal("Менеджер/отдел", employees.Single(e => e.Id == order.ManagerId).Name);
        Assert.Equal(project.Name, Path.GetFileName(plan.Path));
    }

    [Theory]
    [InlineData("bad/name")] [InlineData("CON")] [InlineData("trailing.")]
    [InlineData("..")]
    public async Task InvalidProjectNameIsRejectedInsteadOfSilentlyRewritten(string name)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single() with { Blcb = "005072", Projects = [project with { Name = name }] };
        var error = Assert.Throws<WorkflowValidationException>(() => WorkflowShopFolderPaths.Plan(f.DirectoryPath, order, book.Employees, book.Customers, book.Machines));
        Assert.Contains("не будет", error.Message);
    }

    [Fact]
    public async Task CollidingNamesAndLongNamesAreNotShortenedOrGivenSurpriseSuffixes()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var book = await f.Store.ReadAsync(Token); var duplicate = project with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = project.Name.ToLowerInvariant() };
        var order = book.Orders.Single() with { Blcb = "005072", Projects = [project, duplicate], Items = [new(project.ItemId, 1, project.Id), new(duplicate.ItemId, 2, duplicate.Id)] };
        Assert.Contains("совпадает", Assert.Throws<WorkflowValidationException>(() => WorkflowShopFolderPaths.Plan(f.DirectoryPath, order, book.Employees, book.Customers, book.Machines)).Message);
        order = order with { Projects = [project with { Name = new string('1', 256) }] };
        Assert.Contains("не будет", Assert.Throws<WorkflowValidationException>(() => WorkflowShopFolderPaths.Plan(f.DirectoryPath, order, book.Employees, book.Customers, book.Machines)).Message);
    }

    [Fact]
    public async Task LocalRootMergesWithOtherPersonalSettingsWithoutChangingDatabaseOrCreatingDirectories()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        f.Model.Blcb = "005072"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        using var settings = f.Model.CreateGeneralSettings(); settings.SelectedSectionIndex = 1;
        var root = Path.Combine(f.DirectoryPath, "shop"); settings.ShopRoot = root;
        Assert.Contains(project.Name, settings.ShopFolderPreview); Assert.False(settings.CanRestart);
        preferences.Update(p => p with { DarkTheme = true, EntrySuggestions = new() { Managers = ["Личный список"] } });
        await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.Error); Assert.False(settings.HasChanges);
        Assert.Equal(root, preferences.Load().ShopRoot); Assert.Equal(root, f.Model.ShopRoot);
        Assert.True(preferences.Load().DarkTheme); Assert.Equal("Личный список", Assert.Single(preferences.Load().EntrySuggestions!.Managers));
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, Token)); Assert.False(Directory.Exists(root));
        var restarted = new WorkflowViewModel(f.Store, new(new WorkflowAppUpdater()), preferences: preferences);
        var other = new WorkflowViewModel(f.Store, new(new WorkflowAppUpdater()), preferences: new(Path.Combine(f.DirectoryPath, "another-pc.json")));
        try { Assert.Equal(root, restarted.ShopRoot); Assert.Null(other.ShopRoot); }
        finally { restarted.Detach(); other.Detach(); }
    }

    [Fact]
    public async Task ConflictingLocalEditorsKeepInputAndAlreadySavedSharedFolderIsNotWrittenTwice()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        using var first = f.Model.CreateGeneralSettings(); using var second = f.Model.CreateGeneralSettings();
        first.SelectedSectionIndex = second.SelectedSectionIndex = 1;
        first.ShopRoot = Path.Combine(f.DirectoryPath, "shop-a"); second.ShopRoot = Path.Combine(f.DirectoryPath, "shop-b");
        await first.SaveCommand.ExecuteAsync(null); await second.SaveCommand.ExecuteAsync(null);
        Assert.Contains("другом окне", second.Error); Assert.True(second.HasShopRootChanges);
        Assert.Equal(Path.Combine(f.DirectoryPath, "shop-b"), second.ShopRoot);
        using var combined = f.Model.CreateGeneralSettings(); combined.SelectedSectionIndex = 1;
        combined.ProjectRoot = Path.Combine(f.DirectoryPath, "new-projects"); combined.ShopRoot = Path.Combine(f.DirectoryPath, "shop-c");
        using (var lease = new FileStream(Path.Combine(f.DirectoryPath, "preferences.json.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
        {
            await combined.SaveCommand.ExecuteAsync(null);
            Assert.NotNull(combined.Error); Assert.Contains("проектов сохранена", combined.Status);
            Assert.False(combined.HasRootChanges); Assert.True(combined.HasShopRootChanges);
        }
        var version = (await f.Store.ReadAsync(Token)).Version;
        await combined.SaveCommand.ExecuteAsync(null); Assert.Null(combined.Error); Assert.False(combined.HasChanges);
        Assert.Equal(version, (await f.Store.ReadAsync(Token)).Version); Assert.Equal(combined.ShopRoot, preferences.Load().ShopRoot);
    }

    [Fact]
    public async Task InvalidInputIsCheckedBeforeEitherSaveAndClearingTheRootDoesNotTouchFiles()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        using var settings = f.Model.CreateGeneralSettings(); settings.SelectedSectionIndex = 1;
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
        settings.ProjectRoot = Path.Combine(f.DirectoryPath, "new-projects"); settings.ShopRoot = "relative";
        await settings.SaveCommand.ExecuteAsync(null); Assert.NotNull(settings.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, Token)); Assert.True(settings.HasRootChanges); Assert.True(settings.HasShopRootChanges);
        var root = Path.Combine(f.DirectoryPath, "shop"); Directory.CreateDirectory(root);
        var sentinel = Path.Combine(root, "keep.txt"); await File.WriteAllTextAsync(sentinel, "Не трогать", Token);
        settings.ShopRoot = "  " + root + Path.DirectorySeparatorChar + "  ";
        await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.Error); Assert.Equal(root, settings.ShopRoot);
        Assert.Contains("Общая папка проектов и личный путь", settings.Status);
        settings.ShopRoot = ""; await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.Error);
        Assert.Null(f.Model.ShopRoot); Assert.Equal("Не трогать", await File.ReadAllTextAsync(sentinel, Token));
    }
}
