using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowTrashTests
{
    [Fact]
    public async Task TrashAndRestoreKeepIdentityPricesHistoryAndFilesAndReserveTheOrderNumber()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = before.Orders.Single();
        var archive = order.Specifications.Single().Current.Report!.ArchivePath;
        var paths = new[] { project.NativePath!, ProjectFixture.ReportPath(project), archive };
        var hashes = paths.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        var actor = before.Employees[2].Id; var at = DateTimeOffset.UtcNow;
        var deleted = WorkflowChanges.MoveOrderToTrash(before, order.Id, order.Version, actor, at);
        var trashed = deleted.Orders.Single(); Assert.True(trashed.IsDeleted); Assert.Equal(at, trashed.DeletedAt); Assert.Equal(actor, trashed.DeletedBy);
        Assert.Equal(order.Version + 1, trashed.Version); Assert.Equal(order.ReferenceNumber, trashed.ReferenceNumber);
        Assert.Equal(order.Items, trashed.Items); Assert.Equal(order.Projects, trashed.Projects); Assert.Equal(order.Specifications, trashed.Specifications);
        Assert.Equal(order.CuttingTotal, trashed.CuttingTotal); Assert.Equal(order.Events[0], trashed.Events[0]);
        Assert.Equal(order.CreatedById, trashed.CreatedById);
        Assert.Equal("Перемещена в корзину", trashed.Events[^1].Description);
        Assert.NotNull(WorkflowChanges.MachineRemovalReason(deleted, project.MachineId));
        Assert.Same(deleted, WorkflowChanges.MoveOrderToTrash(deleted, order.Id, trashed.Version, actor, at));
        var next = WorkflowChanges.CreateOrder(deleted, "Другой заказчик", order.ManagerId, actor, actor, order.CreatedAt, out var newId);
        Assert.NotEqual(order.ReferenceNumber, next.Orders.Single(o => o.Id == newId).ReferenceNumber);
        var restored = WorkflowChanges.RestoreOrder(deleted, order.Id, trashed.Version, actor, at.AddMinutes(1));
        var restoredOrder = restored.Orders.Single(); Assert.False(restoredOrder.IsDeleted); Assert.Null(restoredOrder.DeletedBy);
        Assert.Equal(order.Version + 2, restoredOrder.Version); Assert.Equal(order.ReferenceNumber, restoredOrder.ReferenceNumber);
        Assert.Equal(order.CreatedAt, restoredOrder.CreatedAt); Assert.Equal(order.Specifications, restoredOrder.Specifications);
        Assert.Equal(order.CreatedById, restoredOrder.CreatedById);
        Assert.Equal(order.Projects, restoredOrder.Projects); Assert.Equal(order.Items, restoredOrder.Items);
        Assert.Equal("Восстановлена из корзины", restoredOrder.Events[^1].Description);
        Assert.Equal(hashes, paths.Select(p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
        restored.Validate();
    }

    [Fact]
    public async Task TrashedOrdersRejectAllOrderWritesAndConflictsNeverOverwriteAnotherClientsWork()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = book.Orders.Single(); var actor = order.TechnologistId;
        var changed = WorkflowChanges.UpdateOrder(book, order.Id, order.Version, order.ManagerId, actor, "Коллега", 2026, actor, DateTimeOffset.UtcNow);
        Assert.Throws<WorkflowConflictException>(() => WorkflowChanges.MoveOrderToTrash(changed, order.Id, order.Version, actor, DateTimeOffset.UtcNow));
        var deleted = await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.MoveOrderToTrash(w, order.Id, order.Version,
            actor, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        var trash = deleted.Orders.Single(); var revision = trash.Specifications.Single().Current;
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.UpdateOrder(deleted, order.Id, trash.Version, order.ManagerId, actor, "1", 2026, actor, DateTimeOffset.UtcNow));
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.SaveSpecification(deleted, order.Id, trash.Version, project.Id,
            revision.Parameters, revision.DocumentNumber, revision.DocumentDate, actor, DateTimeOffset.UtcNow));
        Assert.Throws<WorkflowValidationException>(() => WorkflowProjects.Add(deleted, order.Id, trash.Version, project, actor, DateTimeOffset.UtcNow));
        Assert.Throws<WorkflowValidationException>(() => WorkflowProjects.Select(deleted, order.Id, trash.Version, project.Id, actor, DateTimeOffset.UtcNow));
        var absent = Path.Combine(f.DirectoryPath, "must-not-be-created");
        await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowProjectFiles.PrepareAsync(absent, trash, "Заказчик",
            deleted.Machines.Single(m => m.Id == project.MachineId), 6, null, DateTimeOffset.Now, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(absent));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Store.WriteOrderAsync(order.Id, order.Version, w => w, TestContext.Current.CancellationToken));
        var restored = await f.Store.WriteOrderAsync(order.Id, trash.Version, w => WorkflowChanges.RestoreOrder(w, order.Id, trash.Version,
            actor, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken, allowTrashed: true);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.Store.WriteOrderAsync(order.Id, trash.Version,
            w => WorkflowChanges.RestoreOrder(w, order.Id, trash.Version, actor, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken, allowTrashed: true));
        Assert.Equal(restored.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
    }

    [Fact]
    public async Task ConfirmationIsRequiredDirtyCardIsKeptAndRestoreReturnsTheSameOrderAndLetter()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); var id = f.Model.SelectedOrder!.Order.Id;
        var draft = f.Model.SelectedDraft!; draft.Coefficient = "1,2";
        Assert.Null(f.Model.CreateTrashConfirmation()); Assert.Contains("Сохраните или отмените", f.Model.Error);
        Assert.Equal("1,2", draft.Coefficient); f.Model.CancelDraftCommand.Execute(null);
        f.Model.ResultText = "Ручное письмо\n";
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var cancelled = f.Model.CreateTrashConfirmation(); Assert.NotNull(cancelled);
        Assert.Equal(before.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
        var confirmation = f.Model.CreateTrashConfirmation()!; await confirmation.ConfirmCommand.ExecuteAsync(null);
        Assert.Null(confirmation.Error); Assert.True(confirmation.WasMoved); Assert.Empty(f.Model.Orders); Assert.False(f.Model.IsOrderOpen);
        f.Model.Search = before.Orders.Single().ReferenceNumber; Assert.Empty(f.Model.Orders);
        var bin = f.Model.CreateTrash(); await bin.ReloadCommand.ExecuteAsync(null); Assert.Null(bin.Error);
        Assert.Equal(id, Assert.Single(bin.Orders).Order.Id); await bin.RestoreCommand.ExecuteAsync(null);
        Assert.Null(bin.Error); Assert.Empty(bin.Orders); Assert.Equal(id, Assert.Single(f.Model.Orders).Order.Id);
        Assert.Equal("Ручное письмо\n", f.Model.ResultText); Assert.True(f.Model.IsCurrentOrderActive);
        Assert.True(File.Exists(project.NativePath));
    }

    [Fact]
    public async Task ForeignDeletionAndRestorationKeepTheDirtyCardAndRequireExplicitReloadOfItsVersion()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var original = f.Model.SelectedDraft!; original.Coefficient = "1,4"; original.Comment = "Не потерять";
        f.Model.ResultText = "Ручное письмо";
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = book.Orders.Single();
        await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.MoveOrderToTrash(w, order.Id, order.Version,
            order.TechnologistId, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        var bin = f.Model.CreateTrash(); await bin.ReloadCommand.ExecuteAsync(null);
        Assert.Same(original, f.Model.SelectedDraft); Assert.Equal("1,4", original.Coefficient);
        Assert.False(f.Model.IsCurrentOrderActive); Assert.True(f.Model.HasCardReview); Assert.Empty(f.Model.Orders);
        await bin.RestoreCommand.ExecuteAsync(null); Assert.Null(bin.Error);
        Assert.Same(original, f.Model.SelectedDraft); Assert.True(f.Model.IsCurrentCardStale); Assert.False(f.Model.SaveSpecificationCommand.CanExecute(null));
        Assert.Equal("Не потерять", original.Comment);
        Assert.Null(f.Model.CreateTrashConfirmation());
        f.Model.DiscardCardChangesCommand.Execute(null);
        Assert.False(f.Model.IsCurrentCardStale); Assert.False(f.Model.HasCardReview);
        Assert.Equal("1", f.Model.SelectedDraft!.Coefficient); Assert.Equal("Ручное письмо", f.Model.ResultText);
    }

    [Fact]
    public async Task ConfirmationAndTrashRowsKeepTheirSnapshotsWhenAnotherClientChangesTheOrder()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var confirmation = f.Model.CreateTrashConfirmation()!;
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = book.Orders.Single();
        book = await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.UpdateOrder(w, order.Id, order.Version,
            order.ManagerId, order.TechnologistId, "Чужая правка", 2026, order.TechnologistId, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await confirmation.ConfirmCommand.ExecuteAsync(null); Assert.NotNull(confirmation.Error); Assert.False(confirmation.WasMoved);
        Assert.False((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single().IsDeleted);
        await f.Model.ReloadCommand.ExecuteAsync(null); confirmation = f.Model.CreateTrashConfirmation()!;
        await confirmation.ConfirmCommand.ExecuteAsync(null); Assert.True(confirmation.WasMoved);
        var bin = f.Model.CreateTrash(); await bin.ReloadCommand.ExecuteAsync(null); var row = Assert.Single(bin.Orders);
        await f.Store.WriteOrderAsync(row.Order.Id, row.Order.Version, w => WorkflowChanges.RestoreOrder(w, row.Order.Id, row.Order.Version,
            order.TechnologistId, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken, allowTrashed: true);
        await bin.RestoreCommand.ExecuteAsync(null); Assert.NotNull(bin.Error); Assert.Single(bin.Orders);
        Assert.Equal(row, bin.Selected); await bin.ReloadCommand.ExecuteAsync(null); Assert.Empty(bin.Orders);
    }

    [Fact]
    public async Task SchemaSixUpgradeBacksUpTheOriginalAndKeepsAllOrdersActiveWithoutTouchingSavedData()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var legacy = JsonSerializer.SerializeToNode(before)!.AsObject(); legacy["SchemaVersion"] = 6;
        foreach (var o in legacy["Orders"]!.AsArray()) { o!.AsObject().Remove("DeletedAt"); o.AsObject().Remove("DeletedBy"); o.AsObject().Remove("CreatedById"); }
        var path = Path.Combine(f.DirectoryPath, "schema-six.json"); var original = legacy.ToJsonString();
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var migrated = await new LocalWorkflowWorkspaceStore(path).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(7, migrated.SchemaVersion); Assert.Equal(before.Version, migrated.Version);
        Assert.All(migrated.Orders, o => Assert.False(o.IsDeleted));
        var old = before.Orders.Single(); var order = migrated.Orders.Single();
        Assert.Equal(old.ReferenceNumber, order.ReferenceNumber); Assert.Equal(old.Version, order.Version);
        Assert.Equal(old.CreatedById, order.CreatedById);
        Assert.Equal(old.Projects, order.Projects); Assert.Equal(old.Items, order.Items); Assert.Equal(old.Events, order.Events);
        Assert.Equal(old.Specifications.Single().Revisions, order.Specifications.Single().Revisions);
        Assert.Equal(original, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(f.DirectoryPath, "schema-six.json.*.bak")), TestContext.Current.CancellationToken));
        Assert.Throws<WorkflowValidationException>(() => (migrated with { Orders = [order with { DeletedAt = DateTimeOffset.UtcNow }] }).Validate());
        var unknown = WorkflowWorkspaceMigration.Upgrade(before with { SchemaVersion = 6, Orders = [old with { CreatedById = null, Events = [] }] });
        Assert.Null(unknown.Orders.Single().CreatedById);
        var trashedUnknown = WorkflowChanges.MoveOrderToTrash(unknown, old.Id, old.Version, old.TechnologistId, DateTimeOffset.UtcNow);
        var restoredUnknown = WorkflowChanges.RestoreOrder(trashedUnknown, old.Id, old.Version + 1, old.TechnologistId, DateTimeOffset.UtcNow);
        Assert.Null(restoredUnknown.Orders.Single().CreatedById); // A deletion/restoration actor is never guessed to be the creator.
    }
}
