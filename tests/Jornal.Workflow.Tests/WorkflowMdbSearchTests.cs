using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowMdbSearchTests
{
    [Fact]
    public async Task LateCancelledReadCannotReplaceLatestSearchOrDiscardOpenCardInput()
    {
        using var f = new ProjectFixture();
        var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() };
        var actor = memory.Book.Employees[0].Id;
        foreach (var customer in new[] { "Альфа", "Бета" })
            memory.Book = WorkflowChanges.CreateOrderFromNames(memory.Book, "Менеджер", customer, actor, DateTimeOffset.Now, out _);
        var wire = new DelayedDatabase(memory);
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var model = new WorkflowViewModel(f.Store, new(new WorkflowAppUpdater()), preferences: preferences, databases: wire);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.Null(await model.ConnectDatabaseAsync(Path.Combine(f.DirectoryPath, "search.mdb"), false, TestContext.Current.CancellationToken));
            model.SelectedOrder = model.Orders.Single(o => o.Customer == "Альфа");
            model.ManagerName = "Несохранённый ввод"; model.ResultText = "Ручное письмо";
            wire.HoldNextRead = true; model.Search = "Альфа";
            var old = model.SearchDatabaseNowAsync();
            await wire.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
            model.Search = "Бета"; var latest = model.SearchDatabaseNowAsync();
            wire.Release.TrySetResult(); await Task.WhenAll(old, latest).WaitAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Бета", Assert.Single(model.Orders).Customer);
            Assert.Contains("Альфа", model.OrderTitle); Assert.Equal("Несохранённый ввод", model.ManagerName);
            Assert.Equal("Ручное письмо", model.ResultText); Assert.True(model.HasPendingEdits); Assert.Null(model.Error);
        }
        finally { wire.Release.TrySetResult(); model.Detach(); }
    }
    private sealed class DelayedDatabase(MemoryMdb memory) : IWorkflowAccessTransport, IWorkflowDatabaseService
    {
        public bool IsAvailable => true;
        public bool HoldNextRead { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IWorkflowWorkspaceStore> OpenAsync(string path, bool create, CancellationToken token = default)
        { var store = new WorkflowMdbStore(path, this, memory.Id); await store.InspectAsync(token); return store; }
        public async Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken token = default)
        {
            var response = await memory.SendAsync(request, token);
            if (request.Operation == WorkflowAccessOperation.Read && HoldNextRead)
            {
                HoldNextRead = false; Started.TrySetResult();
                await Release.Task; // Deliberately ignores cancellation to model a late response.
            }
            return response;
        }
    }
}
