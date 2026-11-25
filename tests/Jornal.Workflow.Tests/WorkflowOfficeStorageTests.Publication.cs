using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed partial class WorkflowOfficeStorageTests
{
    [Fact]
    public void PublicationExplainsUnavailableServiceFolderAndBusyOrderSeparately()
    {
        using var fixture = new ProjectFixture();
        var db = Path.Combine(fixture.DirectoryPath, "Office.mdb");
        var database = Guid.NewGuid();
        var order = Guid.NewGuid();
        var folder = Path.Combine(fixture.DirectoryPath, ".workflow-shop-transfers");
        File.WriteAllText(folder, "Existing file");
        var inaccessible = Assert.Throws<WorkflowValidationException>(() => WorkflowShopPublicationLock.Acquire(db, database, order));
        Assert.Contains(folder, inaccessible.Message);
        Assert.Contains("запись", inaccessible.Message);
        Assert.Equal("Existing file", File.ReadAllText(folder));
        File.Delete(folder);
        using var active = WorkflowShopPublicationLock.Acquire(db, database, order);
        var busy = Assert.Throws<WorkflowValidationException>(() => WorkflowShopPublicationLock.Acquire(db, database, order));
        Assert.Contains("уже выполняется", busy.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforePublicationAllowsAnotherComputerToResume(bool replacing)
    {
        using var fixture = new ShopFixture();
        var token = TestContext.Current.CancellationToken;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        var path = Path.Combine(fixture.Root, "Office.mdb");
        var database = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var first = Assert.Single(await fixture.Discover());
        var relative = WorkflowShopTransfer.PublicationPath(fixture.ShopRoot, first.Destination);
        string? previous = null;
        if (replacing)
        {
            using var active = WorkflowShopPublicationLock.Acquire(path, database, fixture.Order.Id, owner);
            var prepared = Assert.Single(await WorkflowShopTransfer.CopyAsync(fixture.ShopRoot, database.ToString("D"), fixture.Order.Id,
                [ShopFixture.Choose(first)], false, _ => Task.CompletedTask, token: token, publication: active));
            Assert.True(prepared.Success, prepared.Message);
            previous = prepared.DestinationRevision;
            Assert.NotNull(previous);
            await active.UpdateAsync(new(ShopPublicationStep.Confirm, fixture.Project.Id, relative, previous), token);
            await File.WriteAllTextAsync(Path.Combine(fixture.Source, "Лист.cnc"), "Новая УП", token);
        }
        var selection = ShopFixture.Choose(Assert.Single(await fixture.Discover()), previous);
        using (var active = new CancelAfterReserve(WorkflowShopPublicationLock.Acquire(path, database, fixture.Order.Id, owner), cancelled))
        {
            var results = await WorkflowShopTransfer.CopyAsync(fixture.ShopRoot, database.ToString("D"), fixture.Order.Id,
                [selection], replacing, _ => Task.CompletedTask, token: cancelled.Token, publication: active);
            Assert.Empty(results);
            Assert.True(active.RolledBack);
        }
        Assert.Equal(replacing, Directory.Exists(first.Destination));
        if (replacing)
            Assert.Equal("UP bytes", await File.ReadAllTextAsync(Path.Combine(first.Destination, "Лист.cnc"), token));
        Assert.Empty(Directory.EnumerateDirectories(fixture.ShopRoot, ".journal-next-new-*", SearchOption.AllDirectories));

        using var colleague = WorkflowShopPublicationLock.Acquire(path, database, fixture.Order.Id, Guid.NewGuid());
        await colleague.UpdateAsync(new(ShopPublicationStep.Check, fixture.Project.Id, relative, previous), token);
        var repeated = Assert.Single(await WorkflowShopTransfer.CopyAsync(fixture.ShopRoot, database.ToString("D"), fixture.Order.Id,
            [selection], replacing, _ => Task.CompletedTask, token: token, publication: colleague));
        Assert.True(repeated.Success, repeated.Message);
        Assert.Equal(replacing ? "Новая УП" : "UP bytes", await File.ReadAllTextAsync(Path.Combine(first.Destination, "Лист.cnc"), token));
    }

    [Fact]
    public async Task RollbackPreservesAnOlderUnfinishedReservationAndIgnoresOtherAttempts()
    {
        using var fixture = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.DirectoryPath, "Office.mdb");
        var database = Guid.NewGuid(); var order = Guid.NewGuid(); var project = Guid.NewGuid(); var owner = Guid.NewGuid();
        const string relative = "Цех/Проект";
        var old = new string('A', 64);
        var next = new ShopPublicationChange(ShopPublicationStep.Reserve, project, relative, null, new string('B', 64))
            { ReservationId = Guid.NewGuid() };
        using (var prior = WorkflowShopPublicationLock.Acquire(path, database, order, owner))
            await prior.UpdateAsync(new(ShopPublicationStep.Reserve, project, relative, null, old), token);
        using (var active = WorkflowShopPublicationLock.Acquire(path, database, order, owner))
        {
            await active.UpdateAsync(next, token);
            await active.UpdateAsync(next, token);
            var rollback = next with { Step = ShopPublicationStep.Rollback };
            await active.UpdateAsync(rollback with { ReservationId = Guid.NewGuid() }, token);
            await active.UpdateAsync(new(ShopPublicationStep.Check, project, relative, next.PreparedRevision), token);
            await active.UpdateAsync(rollback, token);
            await active.UpdateAsync(rollback, token);
        }
        using var colleague = WorkflowShopPublicationLock.Acquire(path, database, order, Guid.NewGuid());
        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            colleague.UpdateAsync(new(ShopPublicationStep.Check, project, relative, null), token));
        await colleague.UpdateAsync(new(ShopPublicationStep.Check, project, relative, old), token);
    }

    private sealed class CancelAfterReserve(WorkflowShopPublicationLock publication, CancellationTokenSource cancelled) : IWorkflowShopPublication
    {
        public bool SupportsRollback => true;
        public bool RolledBack { get; private set; }
        public async Task UpdateAsync(ShopPublicationChange change, CancellationToken token)
        {
            await publication.UpdateAsync(change, token);
            if (change.Step == ShopPublicationStep.Reserve) cancelled.Cancel();
            if (change.Step == ShopPublicationStep.Rollback) RolledBack = true;
        }
        public void Dispose() => publication.Dispose();
    }
}
