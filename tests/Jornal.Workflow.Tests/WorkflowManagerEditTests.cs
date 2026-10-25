using System.Text.Json;
using Jornal.Workflow.Core;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowManagerEditTests
{
    [Fact]
    public async Task FreeManagerNameSavesAtomicallyWithoutChangingProfileAuthorshipOrPersonalSuggestions()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var original = before.Orders.Single();
        var preferencesPath = Path.Combine(f.DirectoryPath, "preferences.json");
        var preferencesBytes = await File.ReadAllBytesAsync(preferencesPath, TestContext.Current.CancellationToken);
        f.Model.ManagerName = " Вахреева "; Assert.True(f.Model.HasPendingEdits);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await f.Store.ReadAsync(TestContext.Current.CancellationToken)));
        await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var after = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = after.Orders.Single();
        var manager = Assert.Single(after.Employees, e => e.Name == "Вахреева");
        Assert.Equal(manager.Id, order.ManagerId); Assert.Equal(before.Employees.Length + 1, after.Employees.Length);
        Assert.Equal("Вахреева", f.Model.ManagerName); Assert.Equal("Вахреева", Assert.Single(f.Model.Orders).Manager);
        Assert.Equal(original.TechnologistId, order.TechnologistId); Assert.Equal(original.CreatedById, order.CreatedById);
        Assert.Equal(original.TechnologistId, f.Model.CurrentEmployee!.Id); Assert.False(f.Model.HasPendingEdits);
        Assert.Empty(f.Model.ManagerSuggestions); Assert.Equal(original.Version + 1, order.Version);
        Assert.Equal(preferencesBytes, await File.ReadAllBytesAsync(preferencesPath, TestContext.Current.CancellationToken));
        f.Model.ManagerName = " вАхРеЕвА "; await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        Assert.Equal(after.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
        Assert.False(f.Model.HasPendingEdits);
    }

    [Fact]
    public async Task ManagerAddedByAnotherClientIsResolvedAgainstLatestWorkspaceWithoutDuplicatingIt()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var original = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single();
        var other = await f.Store.CreateOrderFromNamesAsync("Вахреева", "Другой заказчик", f.Model.CurrentEmployee!.Id,
            DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
        var existing = Assert.Single(other.Workspace.Employees, e => e.Name == "Вахреева");
        f.Model.ManagerName = "вахреева";
        await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var after = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(other.Workspace.Employees.Length, after.Employees.Length);
        Assert.Equal(existing.Id, after.Orders.Single(o => o.Id == original.Id).ManagerId);
        Assert.Equal("Вахреева", f.Model.ManagerName); Assert.Equal(2, after.Orders.Length);
    }

    [Theory]
    [InlineData("")] [InlineData("Нельзя\nтак")] [InlineData("too-long")]
    public async Task InvalidInputLeavesWorkspaceAndTypedValueIntact(string input)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var bytes = await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken);
        var name = input == "too-long" ? new string('Я', 256) : input;
        f.Model.ManagerName = name; f.Model.Blcb = "Оставить ввод";
        await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        Assert.NotNull(f.Model.Error); Assert.Equal(name, f.Model.ManagerName); Assert.Equal("Оставить ввод", f.Model.Blcb);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StaleOrderDoesNotCreateAPartialManagerOrOverwriteForeignChanges()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var before = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single();
        await f.Store.WriteOrderAsync(before.Id, before.Version, book => WorkflowChanges.UpdateOrder(book, before.Id,
            before.Version, before.ManagerId, before.TechnologistId, "Правка коллеги", before.TechnologistId,
            DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken);
        f.Model.ManagerName = "Не создавать при конфликте";
        await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        Assert.NotNull(f.Model.Error); Assert.Equal("Не создавать при конфликте", f.Model.ManagerName);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken));
    }
}
