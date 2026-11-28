using System.Net;
using System.Net.Http.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed partial class WorkflowOfficeStorageTests
{
    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task SameNewOrderFormQueuesAndSendsOrdersAfterRestart(bool startOffline, bool temporarilyReadOnly)
    {
        using var fixture = new ProjectFixture();
        using var log = new WorkflowWindowTests.BindingLog();
        var token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync();
        var original = await fixture.Store.ReadAsync(token);
        var actor = Assert.Single(original.Orders).TechnologistId;
        var office = new MemoryMdb { Book = original with { Orders = [] } };
        var profile = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "home")).FullName;
        var queue = Path.Combine(profile, "office-creations");
        var settings = new WorkflowOfficeStorageSettings(Address, office.Id, Guid.NewGuid(), true);
        var prefs = new WorkflowPreferences { UseOfficeStorage = true, OfficeStorage = settings, EmployeeId = actor };
        var preferences = new WorkflowPreferencesStore(Path.Combine(profile, "preferences.json"));
        preferences.Save(prefs);
        var offline = false;
        var writesDisabled = false;
        var calls = 0;
        var applied = new List<Guid>();
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            calls++;
            if (offline) throw new HttpRequestException("Нет связи с офисом.");
            var request = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            if (request.Operation == WorkflowAccessOperation.Apply)
            {
                if (writesDisabled)
                    return new(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { error = "remote-writes-disabled" }) };
                applied.Add(Assert.Single(request.Patch!.Orders).Id);
            }
            return Reply(await office.SendAsync(request, cancellation));
        }));
        WorkflowOfficeStorageClient NewClient() => new(settings, StorageToken, profile, http);
        WorkflowViewModel NewModel(WorkflowOfficeStorageClient client) => new(client.CreateStore(), new(new WorkflowAppUpdater()),
            preferences: preferences, initialPreferences: prefs, remoteTransport: client);

        var online = NewModel(NewClient());
        try
        {
            await online.ReloadCommand.ExecuteAsync(null);
            Assert.Null(online.Error);
        }
        finally { online.Detach(); }

        offline = startOffline;
        var home = NewClient();
        var model = NewModel(home);
        var window = new WorkflowWindow(preferences, prefs, journalRefreshClock: TimeProvider.System) { DataContext = model };
        var expectedNames = new[] { "Первый заказчик", "Второй заказчик" };
        Guid[] ids;
        try
        {
            window.Show();
            await WaitUntil(() => !model.IsBusy && (startOffline ? model.ShowOfflineJournal : model.CanStartNewOrder));
            var button = window.FindControl<JournalButton>("NewWorkflowOrder")!;
            var save = window.FindControl<JournalButton>("CreateWorkflowOrder")!;
            foreach (var name in expectedNames)
            {
                Assert.True(button.IsEffectivelyVisible && button.IsEnabled);
                button.Command!.Execute(button.CommandParameter);
                await WaitUntil(() => save.IsEffectivelyVisible);
                Assert.False(save.IsEnabled);
                model.NewManager = "Иванов";
                model.NewCustomer = new string('а', 256);
                Assert.False(model.CanCreateOrder);
                model.NewCustomer = name;
                await WaitUntil(() => save.IsEnabled);
                var beforeSave = calls;
                var alreadyOffline = model.ShowOfflineJournal;
                offline = true;
                Click(window, "CreateWorkflowOrder");
                await WaitUntil(() => !model.IsBusy && !model.IsCreating && model.ShowOfflineJournal);
                if (alreadyOffline) Assert.Equal(beforeSave, calls);
                Assert.Contains(model.QueuedOfficeOrderRows, order => order.Customer == name && order.Status == "Ждёт связи");
            }
            Assert.False(model.HasPendingEdits);
            Assert.Empty(applied);
            Assert.Null(model.Error);
            Assert.True(window.FindControl<ItemsControl>("QueuedOfficeOrders")!.IsEffectivelyVisible);
            var queued = home.ReadQueuedOrders(actor);
            Assert.Equal(expectedNames, queued.Select(order => order.Customer).ToArray());
            Assert.All(queued, order => Assert.True(order.NotSent && order.BaseRevision is null));
            Assert.Empty(home.ReadQueuedOrders(Guid.NewGuid()));
            ids = queued.Select(order => order.Id).ToArray();
            if (OperatingSystem.IsWindows())
            {
                var attributes = File.GetAttributes(queue);
                Assert.True(attributes.HasFlag(FileAttributes.Hidden));
                File.SetAttributes(queue, attributes & ~FileAttributes.Hidden);
            }
        }
        finally { WorkflowTestWindows.Close(window); }

        var restartedClient = NewClient();
        var restarted = NewModel(restartedClient);
        var reopened = new WorkflowWindow(preferences, prefs, journalRefreshClock: TimeProvider.System) { DataContext = restarted };
        try
        {
            reopened.Show();
            await WaitUntil(() => !restarted.IsBusy && restarted.ShowOfflineJournal);
            if (OperatingSystem.IsWindows()) Assert.True(File.GetAttributes(queue).HasFlag(FileAttributes.Hidden));
            Assert.Equal(expectedNames, restarted.QueuedOfficeOrderRows.Select(order => order.Customer).ToArray());
            Assert.False(restarted.IsCreating);
            Assert.Empty(applied);
            offline = false;
            writesDisabled = temporarilyReadOnly;
            await reopened.TickJournalRefreshAsync();
            Assert.False(restarted.ShowOfflineJournal);
            if (temporarilyReadOnly)
            {
                Assert.Empty(applied);
                Assert.Equal(ids, restartedClient.ReadQueuedOrders(actor).Select(order => order.Id));
                Assert.Contains(restarted.QueuedOfficeOrderRows, order => order.Status == "Ждёт доступа");
                writesDisabled = false;
                await restarted.SynchronizeOfficeOrdersAsync(DateTimeOffset.UtcNow.AddMinutes(1), token);
            }
            Assert.Empty(restarted.QueuedOfficeOrderRows);
            Assert.Empty(restartedClient.ReadQueuedOrders(actor));
            Assert.Equal(ids, applied.ToArray());
            Assert.All(office.Book.Orders, order => Assert.False(string.IsNullOrWhiteSpace(order.DisplayNumber)));
            Assert.Equal(ids.Order(), restartedClient.ReadOfflineJournal()!.Workspace.Orders.Select(order => order.Id).Order());
            Assert.Empty(log.Errors);
        }
        finally { WorkflowTestWindows.Close(reopened); }
    }
}
