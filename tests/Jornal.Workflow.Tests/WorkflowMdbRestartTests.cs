using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Jornal.Workflow.Core;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowMdbRestartTests
{
    [Fact]
    public async Task RestartFetchesHistoricalLettersByIdOutsideStartupPeriodAndAcknowledgesOnlyTheRestoredDraft()
    {
        using var f = new ProjectFixture(); var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() };
        var actor = memory.Book.Employees[0].Id;
        memory.Book = WorkflowChanges.CreateOrderFromNames(memory.Book, "Менеджер", "Старая заявка", actor, DateTimeOffset.Now.AddDays(-5), out var id);
        var path = Path.Combine(f.DirectoryPath, "shared.mdb");
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        preferences.Save(new() { DatabasePath = path, DatabaseId = memory.Id, EmployeeId = actor });
        var model = new WorkflowViewModel(new WorkflowMdbStore(path, memory, memory.Id), new(new WorkflowAppUpdater()), preferences: preferences);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null); Assert.Empty(model.Orders);
            var draft = new WorkflowRestartDraft(1, id, "Сохранённое письмо") { DatabaseId = memory.Id,
                Results = [new(id, memory.Book.Orders.Single().Version, "Сохранённое письмо", true, false)] };
            var restart = new WorkflowRestartStore(Path.Combine(f.DirectoryPath, "restart.json")); restart.Save(draft);
            Assert.NotNull(restart.Take(memory.Id, remove: false));
            memory.Requests.Clear(); Assert.True(await model.RestoreRestartDraftAsync(draft, TestContext.Current.CancellationToken));
            Assert.Equal("Сохранённое письмо", model.ResultText); Assert.Contains("Старая заявка", model.OrderTitle); Assert.Null(model.Error);
            Assert.Equal(new[] { id }, Assert.Single(memory.Requests).OrderIds);
            var newer = draft with { ResultText = "Более новое письмо" }; restart.Save(newer);
            Assert.Null(restart.Take(memory.Id, expected: draft));
            Assert.Equal(newer.ResultText, restart.Take(memory.Id, remove: false)!.ResultText);
            Assert.NotNull(restart.Take(memory.Id, expected: newer)); Assert.Null(restart.Take(memory.Id));
        }
        finally { model.Detach(); }
    }
}
