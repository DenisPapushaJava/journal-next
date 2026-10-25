using System.Security.Cryptography;
using Jornal.Workflow.Core;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowTrashPurgeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    internal static async Task TrashCurrentAsync(ProjectFixture f)
    {
        var confirmation = f.Model.CreateTrashConfirmation(); Assert.NotNull(confirmation);
        await confirmation.ConfirmCommand.ExecuteAsync(null); Assert.True(confirmation.WasMoved); Assert.Null(confirmation.Error);
    }
    internal static async Task<Guid> NewOrderAsync(ProjectFixture f, string customer)
    {
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Иванов"; f.Model.NewCustomer = customer;
        await f.Model.CreateOrderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        return f.Model.SelectedOrder!.Order.Id;
    }

    [Fact]
    public async Task SinglePurgeRequiresConfirmationKeepsOtherOrderAndEveryPhysicalFileAndForgetsItsCachedLetter()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var book = await f.Store.ReadAsync(Token); var old = book.Orders.Single();
        var files = new[] { project.NativePath!, ProjectFixture.ReportPath(project), old.Specifications[0].Current.Report!.ArchivePath };
        var hashes = Hashes(files); f.Model.ResultText = "Удаляемое письмо";
        await TrashCurrentAsync(f); var activeId = await NewOrderAsync(f, "Оставить активную"); f.Model.ResultText = "Письмо активной заявки";
        var bin = f.Model.CreateTrash(); var request = bin.CreatePurgeConfirmation(false)!;
        var before = await f.Store.ReadAsync(Token); var oldSelection = bin.Selected;
        Assert.Contains(old.ReferenceNumber, request.Details); Assert.Contains("спецификаций: 1", request.Summary);
        Assert.Equal(before.Version, (await f.Store.ReadAsync(Token)).Version); // Opening or abandoning the dialog does not write.
        await request.ConfirmCommand.ExecuteAsync(null); Assert.True(request.WasPurged); Assert.Null(request.Error);
        var after = await f.Store.ReadAsync(Token); Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(activeId, Assert.Single(after.Orders).Id); Assert.Empty(bin.Orders);
        Assert.Equal(hashes, Hashes(files)); Assert.Equal("Письмо активной заявки", f.Model.ResultText);
        Assert.DoesNotContain(f.Model.CreateRestartDraft().Results!, result => result.OrderId == old.Id);
        Assert.False(bin.CanClearTrash); Assert.False(bin.CanPurgeSelected);
        bin.Selected = oldSelection; // A delayed grid selection must not re-enable actions for a removed row.
        Assert.False(bin.CanPurgeSelected); Assert.False(bin.RestoreCommand.CanExecute(null));
        Assert.Equal(before.Customers, after.Customers); Assert.Equal(before.Employees, after.Employees); Assert.Equal(before.Rates, after.Rates);
    }

    [Fact]
    public async Task ClearUsesFrozenIdsAndKeepsNewlyTrashedAndUnrelatedChanges()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); await TrashCurrentAsync(f);
        await NewOrderAsync(f, "Вторая удалённая"); await TrashCurrentAsync(f);
        var newer = await NewOrderAsync(f, "Новая в корзине после подтверждения");
        var bin = f.Model.CreateTrash(); var request = bin.CreatePurgeConfirmation(true)!;
        Assert.Contains("Заявок: 2", request.Summary);
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single(o => o.Id == newer);
        await f.Store.WriteOrderAsync(newer, order.Version, w => WorkflowChanges.MoveOrderToTrash(w, newer, order.Version,
            order.TechnologistId, DateTimeOffset.UtcNow), Token);
        var active = await f.Store.CreateOrderFromNamesAsync("Менеджер", "Активная параллельная", order.TechnologistId, DateTimeOffset.Now, Token);
        await request.ConfirmCommand.ExecuteAsync(null); Assert.True(request.WasPurged); Assert.Null(request.Error);
        var after = await f.Store.ReadAsync(Token); Assert.Equal(2, after.Orders.Length);
        Assert.Equal(newer, Assert.Single(bin.Orders).Order.Id); Assert.Contains(after.Orders, o => o.Id == active.OrderId && !o.IsDeleted);
        Assert.Contains("Удалено заявок: 2", bin.Status);
    }

    [Fact]
    public async Task RestoredRowRejectsTheEntireBatchAndRequiresExplicitRefreshBeforeAnotherOperation()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); await TrashCurrentAsync(f);
        await NewOrderAsync(f, "Вторая"); await TrashCurrentAsync(f);
        var bin = f.Model.CreateTrash(); var request = bin.CreatePurgeConfirmation(true)!;
        var row = bin.Orders[0];
        await f.Store.WriteOrderAsync(row.Order.Id, row.Order.Version, w => WorkflowChanges.RestoreOrder(w, row.Order.Id,
            row.Order.Version, row.Order.TechnologistId, DateTimeOffset.UtcNow), Token, allowTrashed: true);
        var before = await f.Store.ReadAsync(Token);
        await request.ConfirmCommand.ExecuteAsync(null);
        Assert.False(request.WasPurged); Assert.True(request.RequiresRefresh); Assert.True(bin.RequiresRefresh);
        Assert.NotNull(request.Error); Assert.False(request.CanConfirm); Assert.False(bin.CanPurgeSelected); Assert.False(bin.CanClearTrash);
        Assert.False(bin.RestoreCommand.CanExecute(null)); Assert.Null(bin.CreatePurgeConfirmation(true));
        await request.ConfirmCommand.ExecuteAsync(null); // A stale dialog cannot silently retry.
        Assert.Equal(before.Version, (await f.Store.ReadAsync(Token)).Version); Assert.Equal(2, before.Orders.Length);
        await bin.ReloadCommand.ExecuteAsync(null); Assert.False(bin.RequiresRefresh); Assert.Single(bin.Orders);
        var fresh = bin.CreatePurgeConfirmation(true)!; await fresh.ConfirmCommand.ExecuteAsync(null); Assert.True(fresh.WasPurged);
        Assert.False(Assert.Single((await f.Store.ReadAsync(Token)).Orders).IsDeleted);
    }

    [Fact]
    public async Task ActiveMissingDuplicateOrForeignActorScopeCannotBePurgedAndRepeatingACompletedDeleteDoesNotWrite()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var before = await f.Store.ReadAsync(Token); var order = before.Orders.Single();
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.Store.PurgeTrashedOrdersAsync([new(order.Id, order.Version)], order.TechnologistId, Token));
        await TrashCurrentAsync(f); var book = await f.Store.ReadAsync(Token); order = book.Orders.Single();
        var target = new WorkflowTrashTarget(order.Id, order.Version);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Store.PurgeTrashedOrdersAsync([target, target], order.TechnologistId, Token));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Store.PurgeTrashedOrdersAsync([target], Guid.NewGuid(), Token));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.Store.PurgeTrashedOrdersAsync([target, new(Guid.NewGuid(), 1)], order.TechnologistId, Token));
        Assert.Equal(book.Version, (await f.Store.ReadAsync(Token)).Version);
        var purged = await f.Store.PurgeTrashedOrdersAsync([target], order.TechnologistId, Token); Assert.Empty(purged.Orders);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.Store.PurgeTrashedOrdersAsync([target], order.TechnologistId, Token));
        Assert.Equal(purged.Version, (await f.Store.ReadAsync(Token)).Version);
    }

    [Fact]
    public async Task LockedWorkspaceDoesNotDeleteAnythingAndOnlyRefreshReenablesPurge()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); await TrashCurrentAsync(f);
        var before = await f.Store.ReadAsync(Token); var bin = f.Model.CreateTrash(); var request = bin.CreatePurgeConfirmation(false)!;
        using (var lease = new FileStream(f.Store.FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        { await request.ConfirmCommand.ExecuteAsync(null); Assert.False(request.WasPurged); Assert.NotNull(request.Error); }
        Assert.True(bin.RequiresRefresh); Assert.False(request.CanConfirm);
        Assert.Equal(before.Version, (await f.Store.ReadAsync(Token)).Version);
        await bin.ReloadCommand.ExecuteAsync(null); Assert.True(bin.CanPurgeSelected);
        var fresh = bin.CreatePurgeConfirmation(false)!; await fresh.ConfirmCommand.ExecuteAsync(null);
        Assert.True(fresh.WasPurged); Assert.Empty((await f.Store.ReadAsync(Token)).Orders);
    }

    [Fact]
    public async Task ForeignTrashedDirtyCardIsProtectedAndAnUnrelatedNewOrderDraftSurvivesPurge()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); var draft = f.Model.SelectedDraft!; draft.Coefficient = "1,7";
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single();
        await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.MoveOrderToTrash(w, order.Id, order.Version,
            order.TechnologistId, DateTimeOffset.UtcNow), Token);
        var bin = f.Model.CreateTrash(); await bin.ReloadCommand.ExecuteAsync(null);
        var before = await f.Store.ReadAsync(Token); var blocked = bin.CreatePurgeConfirmation(false)!;
        await blocked.ConfirmCommand.ExecuteAsync(null); Assert.False(blocked.WasPurged); Assert.Contains("черновик", blocked.Error);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("1,7", draft.Coefficient);
        Assert.Equal(before.Version, (await f.Store.ReadAsync(Token)).Version);
        f.Model.DiscardCardChangesCommand.Execute(null); // Explicitly discard the old card, retaining the deleted order itself.
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Не потерять менеджера"; f.Model.NewCustomer = "Новый несохранённый заказчик";
        await bin.ReloadCommand.ExecuteAsync(null); var request = bin.CreatePurgeConfirmation(false)!;
        await request.ConfirmCommand.ExecuteAsync(null); Assert.True(request.WasPurged);
        Assert.True(f.Model.IsCreating); Assert.Equal("Не потерять менеджера", f.Model.NewManager); Assert.Equal("Новый несохранённый заказчик", f.Model.NewCustomer);
        Assert.True(f.Model.HasPendingEdits); Assert.True(File.Exists(project.NativePath));
    }

    private static string[] Hashes(IEnumerable<string> paths) => paths.Select(path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    [Fact]
    public async Task ForeignPermanentDeletionKeepsTheOpenDraftVisibleUntilExplicitDiscard()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var draft = f.Model.SelectedDraft!; draft.Coefficient = "1,8"; f.Model.ResultText = "Сохранить мой текст";
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single();
        book = await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.MoveOrderToTrash(w, order.Id,
            order.Version, order.TechnologistId, DateTimeOffset.UtcNow), Token);
        await f.Store.PurgeTrashedOrdersAsync([new(order.Id, book.Orders.Single().Version)], order.TechnologistId, Token);
        var bin = f.Model.CreateTrash(); await bin.ReloadCommand.ExecuteAsync(null);
        Assert.True(f.Model.IsOrderOpen); Assert.True(f.Model.HasCardReview); Assert.False(f.Model.IsCurrentOrderActive);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("1,8", draft.Coefficient); Assert.Equal("Сохранить мой текст", f.Model.ResultText);
        Assert.False(f.Model.SaveSpecificationCommand.CanExecute(null)); Assert.True(f.Model.HasPendingEdits);
        f.Model.DiscardCardChangesCommand.Execute(null); Assert.False(f.Model.IsOrderOpen); Assert.False(f.Model.HasPendingEdits);
        Assert.Empty((await f.Store.ReadAsync(Token)).Orders); Assert.True(File.Exists(project.NativePath));
    }

    [Fact]
    public async Task InactiveDraftOfAPurgedOrderCanBeReopenedWithoutPuttingItBackIntoStorage()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); var draft = f.Model.SelectedDraft!; draft.Coefficient = "1,9";
        var oldId = f.Model.SelectedOrder!.Order.Id;
        var activeId = await NewOrderAsync(f, "Другая активная"); f.Model.ResultText = "Письмо другой заявки";
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single(o => o.Id == oldId);
        book = await f.Store.WriteOrderAsync(oldId, order.Version, w => WorkflowChanges.MoveOrderToTrash(w, oldId, order.Version,
            order.TechnologistId, DateTimeOffset.UtcNow), Token);
        await f.Store.PurgeTrashedOrdersAsync([new(oldId, book.Orders.Single(o => o.Id == oldId).Version)], order.TechnologistId, Token);
        book = await f.Store.ReadAsync(Token);
        await f.Store.WriteAsync(book.Version, w => WorkflowChanges.UpdateCatalog(w,
            w.Machines.Where(m => m.Id != project.MachineId).ToArray(), w.Rates.Where(r => r.MachineId != project.MachineId).ToArray()), Token);
        await f.Model.CreateTrash().ReloadCommand.ExecuteAsync(null);
        var local = f.Model.Orders.Single(row => row.Order.Id == oldId); Assert.True(local.IsMissing);
        f.Model.SelectedOrder = local;
        Assert.True(f.Model.IsCurrentOrderMissing); Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("1,9", draft.Coefficient);
        Assert.Equal("Лазер", Assert.Single(f.Model.Projects).Machine);
        Assert.False(f.Model.IsCurrentOrderActive); Assert.False(f.Model.SaveSpecificationCommand.CanExecute(null));
        Assert.Equal(activeId, Assert.Single((await f.Store.ReadAsync(Token)).Orders).Id);
        f.Model.DiscardCardChangesCommand.Execute(null);
        Assert.DoesNotContain(f.Model.Orders, row => row.Order.Id == oldId); Assert.Equal("Письмо другой заявки", f.Model.ResultText);
        Assert.False(f.Model.HasPendingEdits);
    }

    [Fact]
    public async Task LetterOnlySnapshotOfAPurgedOrderRemainsVisibleAndBlocksRestartUntilDiscard()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); f.Model.ResultText = "Не потерять письмо";
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single();
        book = await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.MoveOrderToTrash(w, order.Id, order.Version,
            order.TechnologistId, DateTimeOffset.UtcNow), Token);
        await f.Store.PurgeTrashedOrdersAsync([new(order.Id, book.Orders.Single().Version)], order.TechnologistId, Token);
        await f.Model.ReloadCommand.ExecuteAsync(null);
        Assert.True(f.Model.IsCurrentOrderMissing); Assert.True(f.Model.HasCardReview); Assert.True(f.Model.HasPendingEdits);
        Assert.Equal("Не потерять письмо", f.Model.ResultText); Assert.True(Assert.Single(f.Model.Orders).IsMissing);
        f.Model.DiscardCardChangesCommand.Execute(null); Assert.Empty(f.Model.Orders); Assert.False(f.Model.HasPendingEdits);
    }
}
