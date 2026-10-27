using System.Text.Json;
using System.Text.Json.Nodes;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowTechnologistTests
{
    [Fact]
    public async Task ManagerIsNotATechnologistUntilExplicitProfileSaveAndCanHoldBothRolesWithOneId()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        f.Model.ManagerName = "Вахреева"; await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var manager = Assert.Single(before.Employees, e => e.Name == "Вахреева"); Assert.False(manager.IsTechnologist);
        Assert.DoesNotContain(manager.Name, f.Model.CreateGeneralSettings().EmployeeSuggestions);
        Assert.DoesNotContain(f.Model.CreateResponsibleChange()!.Employees, e => e.Id == manager.Id);
        var profile = f.Model.CreateGeneralSettings(); profile.ProfileName = manager.Name;
        await profile.SaveCommand.ExecuteAsync(null); Assert.Null(profile.Error);
        var after = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var person = Assert.Single(after.Employees, e => e.Name == manager.Name);
        Assert.True(person.CanWork); Assert.Equal(manager.Id, person.Id); Assert.Equal(before.Employees.Length, after.Employees.Length);
        Assert.Equal(before.Orders.Single().ManagerId, after.Orders.Single().ManagerId);
        Assert.Equal(before.Orders.Single().TechnologistId, after.Orders.Single().TechnologistId);
    }

    [Fact]
    public async Task SchemaNineUpgradeSeparatesManagerOnlyNamesAndPreservesHistoricalPeopleAndOriginalBytes()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        f.Model.ManagerName = "Только менеджер"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var node = WorkflowTestData.LegacyJson(before, 9);
        foreach (var employee in node["Employees"]!.AsArray())
        { employee!.AsObject().Remove("IsTechnologist"); employee.AsObject().Remove("IsTechnologistArchived"); }
        var original = node.ToJsonString(); var path = Path.Combine(f.DirectoryPath, "schema9.json");
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var store = new LocalWorkflowWorkspaceStore(path); var after = await store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowWorkspace.CurrentSchema, after.SchemaVersion); Assert.Equal(before.Version, after.Version);
        Assert.Equal(JsonSerializer.Serialize(before.Orders), JsonSerializer.Serialize(after.Orders));
        Assert.Equal(before.Employees.Select(e => (e.Id, e.Name, e.ColorArgb)), after.Employees.Select(e => (e.Id, e.Name, e.ColorArgb)));
        Assert.False(after.Employees.Single(e => e.Name == "Только менеджер").IsTechnologist);
        Assert.True(after.Employees.Single(e => e.Id == before.Orders.Single().TechnologistId).IsTechnologist);
        Assert.True(after.Employees.Single(e => e.Name == "Сидоров").IsTechnologist); // An unclassified legacy person is retained.
        var backup = Assert.Single(Directory.GetFiles(f.DirectoryPath, "schema9.json.*.bak"));
        Assert.Equal(original, await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
        var persisted = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        await store.ReadAsync(TestContext.Current.CancellationToken); Assert.Single(Directory.GetFiles(f.DirectoryPath, "schema9.json.*.bak"));
        Assert.Equal(persisted, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Same(after, WorkflowWorkspaceMigration.Upgrade(after));
    }

    [Fact]
    public async Task ArchiveRetainsOrdersDraftsAndIdentityAndExplicitReadditionRestoresSamePerson()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var original = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single();
        var target = f.Model.CurrentEmployee!;
        var choice = f.Model.CreateGeneralSettings(); choice.ProfileName = "Сидоров";
        await choice.SaveCommand.ExecuteAsync(null); Assert.Null(choice.Error);
        f.Model.AddLegacySpecificationDraft(); var draft = f.Model.SelectedDraft!; draft.Comment = "Черновик";
        f.Model.ManagerName = "Черновик менеджера"; f.Model.Blcb = "Не сохранено"; f.Model.ResultText = "Письмо";
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var settings = f.Model.CreateGeneralSettings(); settings.ProjectRoot = Path.Combine(f.DirectoryPath, "unsaved-root");
        settings.ProfileName = "Несохранённый профиль"; var color = settings.ProfileColorArgb;
        await settings.RemoveTechnologistAsync(target, TestContext.Current.CancellationToken); Assert.Null(settings.Error);
        var archived = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JsonSerializer.Serialize(before.Orders), JsonSerializer.Serialize(archived.Orders));
        Assert.True(archived.Employees.Single(e => e.Id == target.Id).IsTechnologistArchived);
        Assert.DoesNotContain(target.Name, settings.EmployeeSuggestions); Assert.Equal("Сидоров", f.Model.CurrentEmployee!.Name);
        Assert.Equal("Несохранённый профиль", settings.ProfileName); Assert.Equal(color, settings.ProfileColorArgb); Assert.True(settings.HasRootChanges);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("Черновик", draft.Comment);
        Assert.Equal("Черновик менеджера", f.Model.ManagerName); Assert.Equal("Не сохранено", f.Model.Blcb); Assert.Equal("Письмо", f.Model.ResultText);
        var oldPreferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "other-preferences.json"));
        oldPreferences.Save(new() { EmployeeId = target.Id });
        var other = new WorkflowViewModel(f.Store, new AppUpdatesViewModel(new WorkflowAppUpdater()));
        try
        {
            other.UseLocalPreferencesStore(oldPreferences); await other.ReloadCommand.ExecuteAsync(null);
            Assert.Null(other.CurrentEmployee); Assert.Equal("Профиль не выбран", other.ProfileName);
            other.NewOrderCommand.Execute(null); Assert.False(other.IsCreating); Assert.NotNull(other.Error);
        }
        finally { other.Detach(); }
        settings.ProfileName = target.Name; await settings.SaveCommand.ExecuteAsync(null); Assert.Null(settings.Error);
        var restored = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, f.Model.CurrentEmployee!.Id); Assert.True(restored.Employees.Single(e => e.Id == target.Id).CanWork);
        Assert.Equal(before.Employees.Length, restored.Employees.Length); Assert.Equal(original.TechnologistId, restored.Orders.Single().TechnologistId);
        Assert.True(settings.HasRootChanges); Assert.Equal("Черновик менеджера", f.Model.ManagerName);
        settings.ProfileName = "Сидоров";
        await settings.RemoveTechnologistAsync(restored.Employees.Single(e => e.Name == "Сидоров"), TestContext.Current.CancellationToken);
        Assert.Null(settings.Error); Assert.Equal(target.Name, settings.ProfileName); Assert.False(settings.HasProfileChanges);
        Assert.Equal(target.Id, f.Model.CurrentEmployee.Id); Assert.True(settings.HasRootChanges);
    }

    [Fact]
    public async Task CurrentProfileAndConcurrentColorChangesAreProtectedAndArchiveCannotBeSilentlyUndone()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var actor = f.Model.CurrentEmployee!;
        var target = book.Employees.Single(e => e.Name == "Сидоров");
        Assert.False(f.Model.CreateGeneralSettings().CanRemoveTechnologist(actor.Name));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Store.ArchiveTechnologistAsync(actor, actor.Id, TestContext.Current.CancellationToken));
        var changed = await f.Store.SaveEmployeeProfileAsync(target, target.Name, 0xFF123456, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.Store.ArchiveTechnologistAsync(target, actor.Id, TestContext.Current.CancellationToken));
        var latest = changed.Employees.Single(e => e.Id == target.Id);
        var archived = await f.Store.ArchiveTechnologistAsync(latest, actor.Id, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.Store.SaveEmployeeProfileAsync(latest, latest.Name, latest.ColorArgb!.Value, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Store.CreateOrderFromNamesAsync("Менеджер", "Заказчик", target.Id, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        Assert.Equal(archived.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
    }

    [Fact]
    public async Task AmbiguousLegacyNamesCannotRemoveAnArbitraryPerson()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        await f.Store.WriteAsync(book.Version, w => w with
        { Employees = [.. w.Employees, new(Guid.NewGuid(), "Сидоров") { IsTechnologist = true }] }, TestContext.Current.CancellationToken);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var settings = f.Model.CreateGeneralSettings();
        Assert.Null(settings.FindTechnologist("Сидоров")); Assert.False(settings.CanRemoveTechnologist("Сидоров"));
    }
}
