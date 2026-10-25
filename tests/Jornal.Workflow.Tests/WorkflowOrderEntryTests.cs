using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowOrderEntryTests
{
    private static WorkflowEntrySuggestionsViewModel Lists(ProjectFixture f) => f.Model.CreateGeneralSettings().CreateEntrySuggestionsEditor();
    private static WorkflowPreferencesStore Preferences(ProjectFixture f) => new(Path.Combine(f.DirectoryPath, "preferences.json"));

    [Fact]
    public async Task PersonalListsNormalizeAndPersistWithoutChangingWorkspaceOrLearningFromOrders()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        Assert.Empty(f.Model.ManagerSuggestions); Assert.Empty(f.Model.CustomerSuggestions);
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken);
        var settings = Lists(f); settings.ManagersText = "  Сидоров  \n\nсидоров\nИванов";
        settings.CustomersText = "ООО, Заказчик\n Второй \nООО, Заказчик";
        await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.Error); Assert.False(settings.HasChanges);
        Assert.Equal(new[] { "Сидоров", "Иванов" }, f.Model.ManagerSuggestions);
        Assert.Equal(new[] { "ООО, Заказчик", "Второй" }, f.Model.CustomerSuggestions);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken));
        var client = new WorkflowViewModel(f.Store, new AppUpdatesViewModel(new WorkflowAppUpdater()));
        try
        {
            client.UseLocalPreferencesStore(Preferences(f)); await client.ReloadCommand.ExecuteAsync(null);
            Assert.Equal(f.Model.ManagerSuggestions, client.ManagerSuggestions);
            client.UseLocalPreferencesStore(new(Path.Combine(f.DirectoryPath, "another-pc.json")));
            await client.ReloadCommand.ExecuteAsync(null); Assert.Empty(client.ManagerSuggestions); Assert.Empty(client.CustomerSuggestions);
        }
        finally { client.Detach(); }
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Новый менеджер"; f.Model.NewCustomer = "Новый заказчик";
        await f.Model.CreateOrderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        Assert.DoesNotContain("Новый менеджер", f.Model.ManagerSuggestions); Assert.DoesNotContain("Новый заказчик", f.Model.CustomerSuggestions);
        settings.ManagersText = ""; settings.CustomersText = ""; await settings.SaveCommand.ExecuteAsync(null);
        Assert.Null(settings.Error); Assert.Empty(Preferences(f).Load().EntrySuggestions!.Managers);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Empty(f.Model.CustomerSuggestions);
    }

    [Fact]
    public async Task ListsMergeWithThemeAndProfileButConflictingListEditorsKeepTheirInput()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var parent = f.Model.CreateGeneralSettings(); var first = parent.CreateEntrySuggestionsEditor(); var second = Lists(f); var preferences = Preferences(f);
        first.ManagersText = "Менеджер А"; second.ManagersText = "Менеджер Б";
        parent.ProfileName = "Другой технолог"; parent.ProjectRoot = Path.Combine(f.DirectoryPath, "unsaved-root");
        preferences.Update(p => p with { DarkTheme = true });
        await first.SaveCommand.ExecuteAsync(null); Assert.Null(first.Error);
        Assert.True(parent.HasProfileChanges); Assert.True(parent.HasRootChanges); Assert.False(first.HasChanges);
        await parent.SaveCommand.ExecuteAsync(null); Assert.Null(parent.Error);
        var saved = preferences.Load(); Assert.True(saved.DarkTheme); Assert.Equal(f.Model.CurrentEmployee!.Id, saved.EmployeeId);
        Assert.Equal("Менеджер А", Assert.Single(saved.EntrySuggestions!.Managers)); Assert.Equal("Менеджер А", Assert.Single(f.Model.ManagerSuggestions));
        await second.SaveCommand.ExecuteAsync(null); Assert.Contains("другом окне", second.Error);
        Assert.Equal("Менеджер Б", second.ManagersText); Assert.True(second.HasChanges);
        second.ManagersText = "Менеджер А"; await second.SaveCommand.ExecuteAsync(null); Assert.Null(second.Error);
        Assert.False(second.HasChanges); Assert.Equal(saved, preferences.Load() with { EntrySuggestions = saved.EntrySuggestions });
        // A list save must not switch the visible identity to another instance's local choice.
        var actor = f.Model.CurrentEmployee.Id;
        preferences.Update(p => p with { EmployeeId = WorkflowTestData.CreateWorkspace().Employees[0].Id });
        second.ManagersText = "Менеджер В"; await second.SaveCommand.ExecuteAsync(null); Assert.Null(second.Error);
        Assert.Equal(actor, f.Model.CurrentEmployee.Id); Assert.Equal(WorkflowTestData.CreateWorkspace().Employees[0].Id, preferences.Load().EmployeeId);
    }

    [Fact]
    public async Task FilesHaveIndependentLeasesAndBusyWritesFailPromptlyWithoutLosingInput()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var lists = Lists(f); lists.ManagersText = "Сохранить ввод";
        using (var workspaceLease = new FileStream(f.Store.FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await lists.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); Assert.Null(lists.Error);
            f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Менеджер"; f.Model.NewCustomer = "Не потерять";
            await f.Model.CreateOrderCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.NotNull(f.Model.Error); Assert.True(f.Model.IsCreating); Assert.Equal("Не потерять", f.Model.NewCustomer);
            Assert.Equal("Менеджер", f.Model.NewManager);
        }
        using (var preferencesLease = new FileStream(Path.Combine(f.DirectoryPath, "preferences.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await f.Model.CreateOrderCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken); Assert.Null(f.Model.Error);
            lists.ManagersText = "Другая правка"; await lists.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.NotNull(lists.Error); Assert.Equal("Другая правка", lists.ManagersText); Assert.True(lists.HasChanges);
        }
        await lists.SaveCommand.ExecuteAsync(null); Assert.Null(lists.Error);
        Assert.Equal("Другая правка", Assert.Single(Preferences(f).Load().EntrySuggestions!.Managers));
    }

    [Fact]
    public async Task ActiveProfileAtSaveCreatesBothAuthorshipAndInitialResponsibilityAndRequiredManagerIsNeverGuessed()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); f.Model.NewOrderCommand.Execute(null);
        Assert.Empty(f.Model.NewManager); f.Model.NewCustomer = "Новая заявка";
        await f.Model.CreateOrderCommand.ExecuteAsync(null); Assert.Contains("менеджера", f.Model.Error);
        Assert.True(f.Model.IsCreating); Assert.Single((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders);
        f.Model.NewManager = " Новый менеджер ";
        var profile = f.Model.CreateGeneralSettings(); profile.ProfileName = "Новый технолог";
        await profile.SaveCommand.ExecuteAsync(null); Assert.Null(profile.Error); Assert.True(f.Model.IsCreating);
        var actor = f.Model.CurrentEmployee!.Id;
        await f.Model.CreateOrderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = book.Orders.Single(o => o.CustomerId == book.Customers.Single(c => c.Name == "Новая заявка").Id);
        Assert.Equal(actor, order.CreatedById); Assert.Equal(actor, order.TechnologistId); Assert.Equal(actor, order.CreatedById);
        Assert.Equal("Новый менеджер", book.Employees.Single(e => e.Id == order.ManagerId).Name);
    }

    [Fact]
    public async Task AtomicCreationUsesLatestNamesAndAllocatesDistinctNumbersWithoutPartialEntities()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var at = DateTimeOffset.UtcNow; var actor = f.Model.CurrentEmployee!.Id;
        var first = await f.Store.CreateOrderFromNamesAsync("Сидоров", "Покупатель", actor, at, TestContext.Current.CancellationToken);
        // Main view still has the earlier snapshot; resolving and allocating happen in the store, against the current one.
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "сидоров"; f.Model.NewCustomer = "покупатель";
        await f.Model.CreateOrderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var second = await f.Store.CreateOrderFromNamesAsync("Сидоров", "Покупатель", actor, at, TestContext.Current.CancellationToken);
        var rows = second.Workspace.Orders.Where(o => o.CustomerId == first.Workspace.Orders[0].CustomerId).ToArray();
        Assert.Equal(3, rows.Length); Assert.Single(rows.Select(o => o.ManagerId).Distinct()); Assert.Equal(3, rows.Select(o => o.ReferenceNumber).Distinct().Count());
        var original = await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Store.CreateOrderFromNamesAsync("Не добавлять", "Покупатель", Guid.NewGuid(), at, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Store.CreateOrderFromNamesAsync("Не добавлять", "bad\nname", actor, at, TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken));
        var book = second.Workspace;
        var ambiguousManager = book with { Employees = [.. book.Employees, new(Guid.NewGuid(), "Сидоров")] };
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.CreateOrderFromNames(ambiguousManager, "Сидоров", "Покупатель", actor, at, out _));
        var ambiguousCustomer = book with { Customers = [.. book.Customers, new(Guid.NewGuid(), "Покупатель")] };
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.CreateOrderFromNames(ambiguousCustomer, "Сидоров", "Покупатель", actor, at, out _));
    }

    [Fact]
    public async Task TwoCreatingClientsNeverLeaveDuplicateNamesOrWaitForEachOtherIndefinitely()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var at = DateTimeOffset.UtcNow; var actor = f.Model.CurrentEmployee!.Id;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> CreateAsync()
        {
            await gate.Task.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                var client = new Jornal.Workflow.Infrastructure.LocalWorkflowWorkspaceStore(f.Store.FilePath);
                await client.CreateOrderFromNamesAsync("Общий менеджер", "Общий заказчик", actor, at, TestContext.Current.CancellationToken);
                return true;
            }
            catch (IOException) { return false; } // Contention is a known failure, never an automatic write retry.
        }
        var first = CreateAsync(); var second = CreateAsync(); gate.SetResult();
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var successful = results.Count(result => result); Assert.InRange(successful, 1, 2);
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); Assert.Equal(1 + successful, book.Orders.Length);
        Assert.Single(book.Employees, e => e.Name == "Общий менеджер"); Assert.Single(book.Customers, c => c.Name == "Общий заказчик");
        Assert.Equal(book.Orders.Length, book.Orders.Select(o => o.ReferenceNumber).Distinct().Count());
        if (successful == 1)
        {
            // The user can explicitly repeat a confirmed failed attempt after the other writer has finished.
            var saved = await f.Store.CreateOrderFromNamesAsync("Общий менеджер", "Общий заказчик", actor, at, TestContext.Current.CancellationToken);
            Assert.Equal(3, saved.Workspace.Orders.Length);
        }
    }

    [Fact]
    public async Task FullUnicodeListsFitAndInvalidEditsLeavePreviousPreferencesIntact()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var preferences = Preferences(f);
        var names = Enumerable.Range(0, 200).Select(i => i.ToString("D3") + new string('Ж', 77)).ToArray();
        preferences.Update(p => p with { EntrySuggestions = new() { Managers = names } });
        Assert.Equal(names, preferences.Load().EntrySuggestions!.Managers);
        var size = new FileInfo(Path.Combine(f.DirectoryPath, "preferences.json")).Length; Assert.InRange(size, 8193, WorkflowPreferencesStore.MaximumBytes);
        var original = await File.ReadAllBytesAsync(Path.Combine(f.DirectoryPath, "preferences.json"), TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(() => preferences.Update(p => p with { EntrySuggestions = new() { Managers = [.. names, "201-й"] } }));
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(f.DirectoryPath, "preferences.json"), TestContext.Current.CancellationToken));
        await f.Model.ReloadCommand.ExecuteAsync(null); var settings = Lists(f); settings.ManagersText = new string('Я', 256);
        await settings.SaveCommand.ExecuteAsync(null); Assert.NotNull(settings.Error); Assert.Equal(new string('Я', 256), settings.ManagersText);
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(f.DirectoryPath, "preferences.json"), TestContext.Current.CancellationToken));
        // Invalid JSON also remains intact and the editor stays usable.
        await File.WriteAllTextAsync(Path.Combine(f.DirectoryPath, "preferences.json"), "{bad", TestContext.Current.CancellationToken);
        settings.ManagersText = "Исправленная строка"; await settings.SaveCommand.ExecuteAsync(null); Assert.NotNull(settings.Error);
        Assert.Equal("Исправленная строка", settings.ManagersText); Assert.True(settings.CanEdit);
    }
}
