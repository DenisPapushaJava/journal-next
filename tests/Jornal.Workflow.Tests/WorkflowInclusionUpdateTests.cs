using System.Collections.Specialized;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

internal sealed class InclusionWriterGate
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls { get; private set; }
    public WorkflowInclusionWrite? Request { get; private set; }
    public bool Fail { get; init; }
    public bool CommitBeforeFailure { get; init; }
    public async Task<WorkflowWorkspace> WriteAsync(LocalWorkflowWorkspaceStore store, WorkflowInclusionWrite request)
    {
        Calls++; Request = request; Entered.TrySetResult();
        await Release.Task;
        if (Fail && !CommitBeforeFailure) throw new IOException("Synthetic write failure");
        var book = await store.WriteOrderAsync(request.OrderId, request.Version, w => WorkflowProjects.SetIncluded(w,
            request.OrderId, request.Version, request.ItemId, request.Included, request.ActorId, request.At));
        if (Fail) throw new IOException("Synthetic response failure after commit");
        return book;
    }
    public ProjectFixture Fixture() => new(store => new WorkflowViewModel(store, new AppUpdatesViewModel(new WorkflowAppUpdater()),
        request => WriteAsync(store, request)));
}

public sealed class WorkflowInclusionUpdateTests
{
    [Fact]
    public async Task ConfirmedInclusionKeepsAllRowAndEditorObjectsAndUsesNoCollectionReset()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var first = await f.AddAsync();
        await ProjectFixture.WriteAsync(first); await f.AcceptAsync(); var variant = await f.AddAsync("Плазма", redo: true);
        var other = await f.AddAsync();
        f.Model.SelectedProject = f.Model.Projects.Single(p => p.Project.Id == first.Id);
        f.Model.ResultText = "Ручное письмо"; f.Model.OrderTabIndex = 0;
        var projects = f.Model.Projects.ToArray(); var orderRow = Assert.Single(f.Model.Orders);
        var draft = f.Model.SelectedDraft; var savedCalculation = f.Model.SelectedDraft?.Saved;
        var resets = new List<string>();
        Observe(f.Model, resets);
        await f.Model.SetProjectIncludedAsync(first.Id, false); Assert.Null(f.Model.Error);
        Assert.Empty(resets); Assert.Same(orderRow, Assert.Single(f.Model.Orders));
        Assert.Equal(projects, f.Model.Projects); Assert.Same(projects[0], f.Model.SelectedProject);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Same(savedCalculation, f.Model.SelectedDraft?.Saved);
        Assert.Equal(0, orderRow.Order.CuttingTotal);
        Assert.False(f.Model.Projects.Single(p => p.Project.Id == first.Id).IsIncluded);
        Assert.False(f.Model.Projects.Single(p => p.Project.Id == variant.Id).IsIncluded);
        Assert.True(f.Model.Projects.Single(p => p.Project.Id == other.Id).IsIncluded);
        Assert.Equal("Ручное письмо", f.Model.ResultText); Assert.True(f.Model.ResultIsStale); Assert.Equal(0, f.Model.OrderTabIndex);
        var saved = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(saved.Orders[0].Version, orderRow.Order.Version);
        await f.Model.SetProjectIncludedAsync(first.Id, true); Assert.Null(f.Model.Error); Assert.Empty(resets);
        Assert.Equal(1105, orderRow.Order.CuttingTotal); Assert.Same(draft, f.Model.SelectedDraft);
        // A following header write proves the acknowledged concurrency version was advanced.
        f.Model.Blcb = "Подтверждённая версия"; await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
    }

    [Fact]
    public async Task PendingWriteBlocksDuplicateMutationsButKeepsLocalInputAndSelectionIntact()
    {
        var gate = new InclusionWriterGate(); using var f = gate.Fixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var original = Assert.Single(f.Model.Orders); var row = Assert.Single(f.Model.Projects);
        var write = f.Model.SetProjectIncludedAsync(project.Id, false);
        try
        {
            await gate.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(f.Model.IsBusy); Assert.False(f.Model.CanUse); Assert.True(f.Model.CanBrowseWorkspace);
            Assert.True(row.IsIncluded); Assert.False(row.DisplayedIncluded); Assert.False(row.CanHitToggle); Assert.True(row.CanToggle);
            await f.Model.SetProjectIncludedAsync(project.Id, true); await f.Model.SaveHeaderCommand.ExecuteAsync(null);
            await f.Model.ReloadCommand.ExecuteAsync(null); f.Model.NewOrderCommand.Execute(null);
            Assert.Equal(1, gate.Calls); Assert.False(f.Model.IsCreating); Assert.Same(original, f.Model.SelectedOrder);
            f.Model.Blcb = "Ввод во время записи"; f.Model.ResultText = "Письмо во время записи";
        }
        finally { gate.Release.TrySetResult(); await write; }
        Assert.Null(f.Model.Error); Assert.Equal("Ввод во время записи", f.Model.Blcb);
        Assert.Equal("Письмо во время записи", f.Model.ResultText); Assert.True(f.Model.ResultIsStale);
        Assert.True(f.Model.CanUse); Assert.True(row.CanHitToggle); Assert.False(row.DisplayedIncluded); Assert.True(f.Model.HasPendingEdits);
        await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        Assert.Equal("Ввод во время записи", (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Blcb);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedWriteRestoresLastConfirmedDisplayWithoutAutomaticRetryAndF5Reconciles(bool committed)
    {
        var gate = new InclusionWriterGate { Fail = true, CommitBeforeFailure = committed };
        using var f = gate.Fixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var row = Assert.Single(f.Model.Projects); var resets = new List<string>(); Observe(f.Model, resets);
        f.Model.ResultText = "Не терять письмо";
        var write = f.Model.SetProjectIncludedAsync(project.Id, false);
        await gate.Entered.Task.WaitAsync(TestContext.Current.CancellationToken); gate.Release.TrySetResult(); await write;
        Assert.Equal(1, gate.Calls); Assert.Contains("подтвердить", f.Model.Error); Assert.Contains("Обновите", f.Model.Error);
        Assert.Empty(resets); Assert.Same(row, Assert.Single(f.Model.Projects)); Assert.True(row.IsIncluded); Assert.True(row.DisplayedIncluded);
        Assert.True(f.Model.CanUse); Assert.False(f.Model.IsBusy); Assert.Equal("Не терять письмо", f.Model.ResultText);
        Assert.Equal(!committed, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Items[0].IsIncluded);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        Assert.Equal(!committed, Assert.Single(f.Model.Projects).IsIncluded); Assert.Equal(1, gate.Calls);
    }

    [Fact]
    public async Task AConcurrentOrderChangeIsNotRebasedOrOverwrittenAndExplicitReloadAllowsRetry()
    {
        var gate = new InclusionWriterGate(); using var f = gate.Fixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var row = Assert.Single(f.Model.Projects); var resets = new List<string>(); Observe(f.Model, resets);
        var pending = f.Model.SetProjectIncludedAsync(project.Id, false);
        try
        {
            await gate.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = book.Orders[0];
            await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.UpdateOrder(w, order.Id,
                order.Version, order.ManagerId, order.TechnologistId, "Правка коллеги", order.TechnologistId, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        }
        finally { gate.Release.TrySetResult(); await pending; }
        Assert.NotNull(f.Model.Error); Assert.True(row.IsIncluded); Assert.True(row.DisplayedIncluded); Assert.Empty(resets);
        var actual = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0];
        Assert.Equal("Правка коллеги", actual.Blcb); Assert.True(actual.Items[0].IsIncluded); Assert.Equal(1, gate.Calls);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Null(f.Model.Error); Assert.Equal(actual.Blcb, f.Model.Blcb);
        await f.Model.SetProjectIncludedAsync(project.Id, false); Assert.Null(f.Model.Error); Assert.Equal(2, gate.Calls);
        actual = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0];
        Assert.Equal("Правка коллеги", actual.Blcb); Assert.False(actual.Items[0].IsIncluded);
    }

    [Fact]
    public async Task AnUnchangedInclusionDoesNotStartAWriteOrMakeTheLetterStale()
    {
        var gate = new InclusionWriterGate(); gate.Release.TrySetResult(); using var f = gate.Fixture();
        await f.InitializeAsync(); var project = await f.AddAsync(); f.Model.ResultText = "Моё письмо";
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken);
        await f.Model.SetProjectIncludedAsync(project.Id, true);
        Assert.Equal(0, gate.Calls); Assert.False(f.Model.IsBusy); Assert.False(f.Model.ResultIsStale);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OtherOrdersAndTheirDirtyCachedCardsAreNotLostWhenTheStoreReturnsAFreshWorkspace()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); var firstId = f.Model.SelectedOrder!.Order.Id;
        f.Model.SelectedDraft!.Comment = "Черновик первой заявки";
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Иванов"; f.Model.NewCustomer = "Вторая заявка";
        await f.Model.CreateOrderCommand.ExecuteAsync(null); var otherProject = await f.AddAsync();
        var firstRow = f.Model.Orders.Single(r => r.Order.Id == firstId); var rows = f.Model.Orders.ToArray();
        await f.Model.SetProjectIncludedAsync(otherProject.Id, false); Assert.Null(f.Model.Error);
        Assert.All(rows, row => Assert.Same(row, f.Model.Orders.Single(r => r.Order.Id == row.Order.Id)));
        Assert.True(firstRow.HasDraft); f.Model.SelectedOrder = firstRow;
        Assert.Equal("Черновик первой заявки", f.Model.SelectedDraft!.Comment); Assert.True(f.Model.HasPendingEdits);
    }

    internal static void Observe(WorkflowViewModel model, List<string> resets)
    {
        void Watch(string name, INotifyCollectionChanged collection) => collection.CollectionChanged += (_, e) =>
        { if (e.Action == NotifyCollectionChangedAction.Reset) resets.Add(name); };
        Watch("Orders", model.Orders); Watch("Projects", model.Projects); Watch("Drafts", model.Drafts);
    }
}
