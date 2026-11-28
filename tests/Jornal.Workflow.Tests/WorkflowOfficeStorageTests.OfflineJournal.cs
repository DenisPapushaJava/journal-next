using System.Net.Http.Json;
using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Jornal.Workflow.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed partial class WorkflowOfficeStorageTests
{
    [AvaloniaFact]
    public async Task ClosingDuringInitialOfficeReadCancelsItAndKeepsTheOfflineJournal()
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var book = await fixture.Store.ReadAsync(TestContext.Current.CancellationToken);
        var order = Assert.Single(book.Orders);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = true;
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            if (slow)
            {
                started.TrySetResult();
                try { await release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled.TrySetResult(); throw; }
            }
            return new(HttpStatusCode.ServiceUnavailable);
        }));
        var settings = new WorkflowOfficeStorageSettings(Address, Guid.NewGuid(), Guid.NewGuid(), true);
        var prefs = new WorkflowPreferences { OfficeStorage = settings, UseOfficeStorage = true, EmployeeId = order.TechnologistId };
        var preferences = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "home", "preferences.json"));
        preferences.Save(prefs);
        var client = new WorkflowOfficeStorageClient(settings, StorageToken, preferences.DirectoryPath, http);
        client.RememberOfflineJournal(book, DateTimeOffset.UtcNow);
        WorkflowWindow NewWindow(out WorkflowViewModel model)
        {
            model = new(client.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences, initialPreferences: prefs, remoteTransport: client);
            return new WorkflowWindow(preferences, prefs, journalRefreshClock: TimeProvider.System) { DataContext = model };
        }

        var window = NewWindow(out var first);
        try
        {
            window.Show();
            await WaitUntil(() => started.Task.IsCompleted && first.IsBusy);
            window.Close();
            Assert.False(window.IsVisible);
            await WaitUntil(() => cancelled.Task.IsCompleted && !first.IsBusy);
        }
        finally { release.TrySetResult(); await WaitUntil(() => !first.IsBusy); WorkflowTestWindows.Close(window); }

        slow = false;
        var reopened = NewWindow(out var second);
        try
        {
            reopened.Show();
            await WaitUntil(() => !second.IsBusy && second.ShowOfflineJournal);
            Assert.Equal(order.Id, Assert.Single(second.OfflineJournal!.Orders).Id);
            reopened.Close();
            Assert.False(reopened.IsVisible);
        }
        finally { WorkflowTestWindows.Close(reopened); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineJournalShowsSavedCardsAndOpensOwnedFoldersUntilOfficeReadSucceeds(bool databaseUnavailable)
    {
        using var fixture = new WorkingFiles();
        using var projects = new ProjectFixture();
        using var log = new WorkflowWindowTests.BindingLog();
        await projects.InitializeAsync();
        var project = await projects.AddAsync();
        var token = TestContext.Current.CancellationToken;
        var office = new MemoryMdb { Book = await projects.Store.ReadAsync(token) };
        var order = Assert.Single(office.Book.Orders);
        var settings = fixture.Client.Settings;
        var prefs = new WorkflowPreferences(databaseUnavailable) { UseOfficeStorage = true, OfficeStorage = settings, EmployeeId = order.TechnologistId };
        var preferences = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "preferences.json"));
        preferences.Save(prefs);
        var offline = false;
        var calls = 0;
        fixture.ReadDatabase = request =>
        {
            calls++;
            if (offline) throw new HttpRequestException("Связь с офисом потеряна.");
            return office.SendAsync(request with { DatabaseId = office.Id }, token).GetAwaiter().GetResult() with { DatabaseId = settings.DatabaseId };
        };
        fixture.Office["Проект.rcam"] = "office";
        var working = await fixture.Client.OpenWorkingCopyAsync(order.Id, project.Id, token);
        WorkflowViewModel NewModel() => new(fixture.Client.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences,
            initialPreferences: prefs, remoteTransport: fixture.Client);
        var previous = NewModel();
        try
        {
            await previous.ReloadCommand.ExecuteAsync(null);
            Assert.Null(previous.Error);
            Assert.Single(previous.Orders);
        }
        finally { previous.Detach(); }
        if (databaseUnavailable) fixture.DatabaseStatus = HttpStatusCode.ServiceUnavailable;
        else offline = true;

        var model = NewModel();
        var window = new OfflineFolderTestWindow(preferences, prefs) { DataContext = model };
        try
        {
            window.Show();
            await WaitUntil(() => !model.IsBusy && model.ShowOfflineJournal);
            var journal = Assert.IsType<WorkflowOfflineJournal>(model.OfflineJournal);
            Assert.Contains("Офлайн · данные от", journal.Status);
            Assert.False(window.FindControl<DataGrid>("WorkflowOrdersGrid")!.IsEffectivelyVisible);
            Assert.True(window.FindControl<Grid>("OfflineJournalPanel")!.IsEffectivelyVisible);
            Assert.False(window.FindControl<Button>("WorkflowCatalog")!.IsEffectivelyVisible);
            var saved = Assert.Single(journal.Orders);
            Assert.Equal(order.DisplayNumber, saved.Number);
            Assert.Equal(project.Name, Assert.Single(saved.Projects).Name);
            var before = calls;
            journal.Search = "нет такой заявки";
            Assert.False(journal.HasOrders);
            journal.Search = saved.Customer;
            Assert.Equal(saved, Assert.Single(journal.Orders));
            var grid = window.FindControl<DataGrid>("OfflineOrdersGrid")!;
            grid.SelectedItem = saved;
            await WaitUntil(() => journal.Selected == saved);
            JournalButton? open = null;
            await WaitUntil(() => (open = window.GetVisualDescendants().OfType<JournalButton>()
                .SingleOrDefault(button => button.Label == "Открыть папку" && button.IsEffectivelyVisible)) is not null);
            open!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => window.OpenedFolder == working.Folder);
            Assert.Equal(before, calls);
            Assert.Empty(fixture.Uploads);

            office.Book = office.Book with { Version = office.Book.Version + 1, Orders = [order with { Blcb = "БЛЦБ-новая", Version = order.Version + 1 }] };
            offline = false;
            fixture.DatabaseStatus = null;
            var refresh = window.FindControl<Button>("RefreshWorkflowJournal")!;
            Assert.True(refresh.IsEnabled && refresh.IsEffectivelyVisible);
            Assert.Same(model.ReloadCommand, refresh.Command);
            refresh.Command!.Execute(refresh.CommandParameter);
            await WaitUntil(() => !model.IsBusy && !model.ShowOfflineJournal);
            Assert.Equal("БЛЦБ-новая", Assert.Single(model.Orders).Order.Blcb);
            Assert.True(window.FindControl<DataGrid>("WorkflowOrdersGrid")!.IsEffectivelyVisible);
            Assert.True(log.Errors.Count == 0, string.Join(Environment.NewLine, log.Errors));
        }
        finally { WorkflowTestWindows.Close(window); }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task OfflineJournalWithEmptyQueueReopensAfterReconnect(bool canWrite, bool hasTechnologist)
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var original = await fixture.Store.ReadAsync(token);
        var actor = Assert.Single(original.Orders).TechnologistId;
        var office = new MemoryMdb { Book = original };
        var offline = false;
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            if (offline) throw new HttpRequestException("Нет связи.");
            var request = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            return Reply(await office.SendAsync(request, cancellation));
        }));
        var profile = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "review")).FullName;
        var settings = new WorkflowOfficeStorageSettings(Address, office.Id, Guid.NewGuid(), canWrite);
        var prefs = new WorkflowPreferences { UseOfficeStorage = true, OfficeStorage = settings, EmployeeId = hasTechnologist ? actor : null, AutomaticJournalRefresh = true };
        var preferences = new WorkflowPreferencesStore(Path.Combine(profile, "preferences.json"));
        preferences.Save(prefs);
        var client = new WorkflowOfficeStorageClient(settings, StorageToken, profile, http);
        var model = new WorkflowViewModel(client.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences, initialPreferences: prefs, remoteTransport: client);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null);
            offline = true;
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.True(model.ShowOfflineJournal);
            var old = Assert.Single(original.Orders);
            office.Book = original with { Version = original.Version + 1, Orders = [old with { Version = old.Version + 1, Blcb = "БЛЦБ-актуальная" }] };
            offline = false;
            var result = await model.RefreshAutomaticallyAsync(true, token: token);
            Assert.Equal(WorkflowRefreshOutcome.Deferred, result);
            var before = office.Requests.Count;
            await model.SynchronizeOfficeOrdersAsync(DateTimeOffset.UtcNow, token);
            Assert.True(office.Requests.Count > before);
            Assert.False(model.ShowOfflineJournal);
            Assert.Equal("БЛЦБ-актуальная", Assert.Single(model.Orders).Order.Blcb);
            Assert.DoesNotContain(office.Requests, request => request.Operation == WorkflowAccessOperation.Apply);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public async Task QueueAfterMidnightProvidesAnOpenableConfirmation()
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var original = await fixture.Store.ReadAsync(token);
        var actor = Assert.Single(original.Orders).TechnologistId;
        var office = new MemoryMdb { Book = original };
        var offline = false;
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            if (offline) throw new HttpRequestException("Нет связи.");
            var request = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            return Reply(await office.SendAsync(request, cancellation));
        }));
        var profile = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "review")).FullName;
        var settings = new WorkflowOfficeStorageSettings(Address, office.Id, Guid.NewGuid(), true);
        var prefs = new WorkflowPreferences { UseOfficeStorage = true, OfficeStorage = settings, EmployeeId = actor };
        var preferences = new WorkflowPreferencesStore(Path.Combine(profile, "preferences.json"));
        preferences.Save(prefs);
        var client = new WorkflowOfficeStorageClient(settings, StorageToken, profile, http);
        var model = new WorkflowViewModel(client.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences, initialPreferences: prefs, remoteTransport: client);
        var id = Guid.NewGuid();
        try
        {
            await model.ReloadCommand.ExecuteAsync(null);
            offline = true;
            using (client.LockCreation(actor))
                client.SaveQueuedOrder(actor, new(id, "Иванов", "Вчерашняя заявка", DateTimeOffset.Now.AddDays(-1), NotSent: true));
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.Single(model.QueuedOfficeOrderRows);
            Assert.True(model.ShowOfflineJournal);
            offline = false;
            await model.SynchronizeOfficeOrdersAsync(DateTimeOffset.UtcNow, token);
            Assert.Contains(office.Book.Orders, order => order.Id == id);
            Assert.Empty(model.QueuedOfficeOrderRows);
            Assert.True(model.IsTodaySelected);
            Assert.DoesNotContain(model.Orders, row => row.Order.Id == id);
            Assert.Null(model.Error);
            var queued = office.Book.Orders.Single(order => order.Id == id);
            Assert.Contains(queued.DisplayNumber, model.OfficeCreationNotice);
            await model.OpenLastOfficeCreationAsync(token);
            Assert.True(model.IsTodaySelected);
            Assert.Contains(queued.DisplayNumber, model.OrderTitle);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public async Task UnavailableLocalCacheDoesNotBlockAnyOrders()
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var original = await fixture.Store.ReadAsync(token);
        var actor = Assert.Single(original.Orders).TechnologistId;
        var office = new MemoryMdb { Book = original };
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            return Reply(await office.SendAsync(request, cancellation));
        }));
        var profile = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "review")).FullName;
        var model = RemoteCreationModel(office, actor, profile, http);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null);
            var cacheFile = Path.Combine(profile, $".office-journal-{office.Id:N}.json");
            File.Delete(cacheFile);
            Directory.CreateDirectory(cacheFile);
            model.NewOrderCommand.Execute(null);
            model.NewManager = "Иванов";
            model.NewCustomer = "Сохранена в офисе";
            await model.CreateOrderCommand.ExecuteAsync(null);
            Assert.Equal(2, office.Book.Orders.Length);
            Assert.Empty(model.QueuedOfficeOrderRows);
            model.NewOrderCommand.Execute(null);
            model.NewManager = "Иванов";
            model.NewCustomer = "Следующая заявка";
            await model.CreateOrderCommand.ExecuteAsync(null);
            Assert.Empty(model.QueuedOfficeOrderRows);
            Assert.Equal(3, office.Book.Orders.Length);
            Assert.Equal(2, office.Requests.Count(request => request.Operation == WorkflowAccessOperation.Apply));
            Assert.Null(model.Error);
        }
        finally { model.Detach(); }
    }


    [Theory]
    [InlineData(true, "нет")]
    [InlineData(true, "пакет уже принят")]
    [InlineData(true, "папка обмена появилась")]
    [InlineData(true, "папка и офис появились")]
    [InlineData(false, "нет")]
    [InlineData(false, "заявка")]
    [InlineData(false, "спецификация")]
    public async Task ColdOfflineCalculationShowsQueuedTotalAndChecksOfficeAfterRestart(bool exchangeEnabled, string officeChange)
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var project = await fixture.AddAsync();
        var token = TestContext.Current.CancellationToken;
        var book = await fixture.Store.ReadAsync(token);
        var office = new MemoryMdb { Book = book };
        using var files = new WorkingFiles(office.Id);
        var drafts = Path.Combine(files.DirectoryPath, "office-calculation-drafts");
        var order = Assert.Single(book.Orders);
        var settings = files.Client.Settings;
        var exchange = Path.Combine(files.DirectoryPath, "exchange");
        var exchangeWasMissing = officeChange.StartsWith("папка", StringComparison.Ordinal);
        if (!exchangeWasMissing) Directory.CreateDirectory(exchange);
        var prefs = new WorkflowPreferences
        {
            UseOfficeStorage = true, OfficeStorage = settings, EmployeeId = order.TechnologistId, CalculationExchangeRoot = exchangeEnabled ? exchange : null
        };
        var preferences = new WorkflowPreferencesStore(Path.Combine(files.DirectoryPath, "preferences.json"));
        preferences.Save(prefs);
        files.ReadDatabase = request => office.SendAsync(request with { DatabaseId = office.Id }, token).GetAwaiter().GetResult()
            with { DatabaseId = settings.DatabaseId };
        files.Office["Проект.rcam"] = "office";
        var officeFile = ProjectFixture.ReportPath(project);
        var name = Path.GetFileName(officeFile);
        files.PrepareCalculation = async (preparation, cancellation) =>
        {
            var submitted = Assert.IsType<WorkflowOfficeReportInput>(preparation.Report);
            await File.WriteAllTextAsync(officeFile, files.Office[name], cancellation);
            return new(officeFile, await WorkflowReportFiles.ArchiveWorkingReportAsync(officeFile, submitted.Sha256, submitted.Baseline, cancellation));
        };
        var copy = await files.Client.OpenWorkingCopyAsync(order.Id, project.Id, token);
        await File.WriteAllTextAsync(Path.Combine(copy.Folder, name), ProjectFixture.Html(project), token);
        files.Client.RememberOfflineJournal(book, DateTimeOffset.UtcNow);
        files.DatabaseStatus = HttpStatusCode.ServiceUnavailable;
        var model = new WorkflowViewModel(files.Client.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences,
            initialPreferences: prefs, remoteTransport: files.Client);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null);
            var journal = Assert.IsType<WorkflowOfflineJournal>(model.OfflineJournal);
            journal.Selected = Assert.Single(journal.Orders);
            Assert.True(model.SelectOfflineCalculation(journal.Selected));
            Assert.Empty(files.Client.PendingCalculationOrders(token));
            var review = Assert.IsType<WorkflowReportReviewViewModel>(await model.CalculateOrderAsync(token));
            Assert.True(review.QueuedForOffice);
            Assert.Null(review.Error);
            Assert.Null(model.Error);
            Assert.False(await model.FinalizeCalculationAsync(review, token));
            var pending = Assert.IsType<WorkflowOfficeCalculationDraft>(files.Client.ReadCalculationDraft(order.Id));
            Assert.Equal(order.Id, Assert.Single(files.Client.PendingCalculationOrders(token)));
            Assert.Contains(SpecificationDraft.Money(pending.PendingOfficeTotal()), journal.SelectedPendingCalculation);
            Assert.Contains(exchangeWasMissing ? "Яндекс Диск недоступен" : exchangeEnabled ? "Сервер проверит" : "когда появится связь", model.CalculationExchangeStatus);
            if (exchangeWasMissing) Assert.False(Directory.Exists(exchange));
            else Assert.Equal(exchangeEnabled ? 1 : 0, Directory.GetFiles(exchange, "*" + WorkflowCalculationExchange.Extension).Length);
            Assert.True(files.Client.HasCalculationDraft(order.Id));
            if (OperatingSystem.IsWindows())
            {
                var attributes = File.GetAttributes(drafts);
                Assert.True(attributes.HasFlag(FileAttributes.Hidden));
                File.SetAttributes(drafts, attributes & ~FileAttributes.Hidden);
            }
            Assert.Empty(office.Requests);
            Assert.Empty(files.Uploads);
        }
        finally { await DetachAndWaitForFileCheckAsync(model, files.Client); }

        var resumedClient = files.ReopenClient();
        var restarted = new WorkflowViewModel(resumedClient.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences,
            initialPreferences: prefs, remoteTransport: resumedClient);
        try
        {
            await restarted.ReloadCommand.ExecuteAsync(null);
            if (OperatingSystem.IsWindows()) Assert.True(File.GetAttributes(drafts).HasFlag(FileAttributes.Hidden));
            var journal = Assert.IsType<WorkflowOfflineJournal>(restarted.OfflineJournal);
            journal.Selected = Assert.Single(journal.Orders);
            Assert.Contains("К отправке", journal.SelectedPendingCalculation);
            var time = DateTimeOffset.UtcNow;
            if (exchangeWasMissing) Directory.CreateDirectory(exchange);
            if (officeChange == "папка обмена появилась")
            {
                await restarted.SynchronizeOfficeOrdersAsync(time.AddMinutes(-2), token);
                var exported = Assert.Single(Directory.GetFiles(exchange));
                Assert.Contains("Сервер проверит", restarted.CalculationExchangeStatus);
                await restarted.SynchronizeOfficeOrdersAsync(time.AddMinutes(-1), token);
                Assert.Equal(exported, Assert.Single(Directory.GetFiles(exchange)));
                Assert.Empty(office.Requests);
            }
            var packageFirst = officeChange is "пакет уже принят" or "папка обмена появилась";
            if (packageFirst) await ReceivePackageAsync();
            if (officeChange == "заявка")
                office.Book = book with { Version = book.Version + 1, Orders = [order with { Version = order.Version + 1, Blcb = "Свежее в офисе" }] };
            if (officeChange == "спецификация") files.Office[name] = ProjectFixture.Html(project, 15000);
            files.DatabaseStatus = null;
            await restarted.SynchronizeOfficeOrdersAsync(time, token);
            await restarted.SynchronizeOfficeOrdersAsync(time.AddMinutes(1), token);
            if (officeChange == "папка и офис появились") Assert.Empty(Directory.GetFiles(exchange));
            else if (exchangeEnabled && !packageFirst) await ReceivePackageAsync();
            Assert.False(restarted.ShowOfflineJournal);
            var officeChanged = officeChange is "заявка" or "спецификация";
            Assert.Equal(officeChanged, resumedClient.HasCalculationDraft(order.Id));
            Assert.Equal(officeChanged ? new[] { order.Id } : [], resumedClient.PendingCalculationOrders(token));
            Assert.Equal(officeChanged ? 0 : 1, office.Requests.Count(request => request.Operation == WorkflowAccessOperation.Apply));
            Assert.Equal(officeChanged || packageFirst ? 0 : 1, files.Uploads.Count);
            if (officeChange == "заявка") Assert.Equal("Свежее в офисе", Assert.Single(office.Book.Orders).Blcb);
            if (officeChange == "спецификация") Assert.Contains("15000", files.Office[name]);
            if (!officeChanged) Assert.Single(Assert.Single(restarted.Orders).Order.Specifications);
        }
        finally { await DetachAndWaitForFileCheckAsync(restarted, resumedClient); }

        async Task DetachAndWaitForFileCheckAsync(WorkflowViewModel viewModel, WorkflowOfficeStorageClient client)
        {
            viewModel.Detach();
            // The file check takes the same gate as the cancelled background read.
            await client.HasPendingFilesAsync(project.Id, prefs.OfficeCopiesRoot, project.NativePath, CancellationToken.None);
        }

        async Task ReceivePackageAsync()
        {
            var package = Assert.Single(Directory.GetFiles(exchange, "*" + WorkflowCalculationExchange.Extension));
            File.SetLastWriteTimeUtc(package, DateTime.UtcNow.AddMinutes(-1));
            var database = Path.Combine(fixture.DirectoryPath, "office.mdb");
            await File.WriteAllTextAsync(database, "", token);
            var worker = new OfficeCalculationInbox(exchange, database, book.ProjectRoot!, office,
                NullLogger<OfficeCalculationInbox>.Instance, new());
            await worker.ProcessAvailableAsync(token);
            Assert.False(File.Exists(package));
            Assert.True(File.Exists(Path.Combine(exchange, "Принятые", Path.GetFileName(package))));
        }
    }

    [Fact]
    public async Task SearchOutageAndReconnectKeepAnUnsavedLiveCard()
    {
        using var fixture = new WorkingFiles();
        using var projects = new ProjectFixture();
        await projects.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var office = new MemoryMdb { Book = await projects.Store.ReadAsync(token) };
        var order = Assert.Single(office.Book.Orders);
        var settings = fixture.Client.Settings;
        var prefs = new WorkflowPreferences { UseOfficeStorage = true, OfficeStorage = settings, EmployeeId = order.TechnologistId };
        var preferences = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "prefs.json"));
        preferences.Save(prefs);
        fixture.ReadDatabase = request => office.SendAsync(request with { DatabaseId = office.Id }, token).GetAwaiter().GetResult()
            with { DatabaseId = settings.DatabaseId };
        var model = new WorkflowViewModel(fixture.Client.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences,
            initialPreferences: prefs, remoteTransport: fixture.Client);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.Null(model.Error);
            Assert.NotNull(fixture.Client.ReadOfflineJournal());
            model.OpenOrderCommand.Execute(null);
            model.Blcb = "БЛЦБ-черновик";
            Assert.True(model.HasPendingEdits);
            fixture.DatabaseStatus = HttpStatusCode.ServiceUnavailable;
            await model.SearchDatabaseNowAsync();
            Assert.True(model.ShowOfflineJournal);
            Assert.False(model.CanRefreshAutomatically);
            fixture.DatabaseStatus = null;
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.False(model.ShowOfflineJournal);
            Assert.Equal("БЛЦБ-черновик", model.Blcb);
            Assert.True(model.HasPendingEdits);
            Assert.DoesNotContain(office.Requests, request => request.Operation == WorkflowAccessOperation.Apply);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public async Task OfflineSnapshotIsScopedToTheConnectionAndIsIgnoredAfterServerRejectsTheKey()
    {
        using var fixture = new WorkingFiles();
        using var projects = new ProjectFixture();
        await projects.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var book = await projects.Store.ReadAsync(token);
        fixture.Client.RememberOfflineJournal(book, DateTimeOffset.UtcNow);
        Assert.NotNull(fixture.Client.ReadOfflineJournal());
        fixture.Client.RememberOfflineJournal(book with { Orders = [] }, DateTimeOffset.UtcNow);
        Assert.NotNull(fixture.Client.ReadOfflineJournal());
        using var http = new HttpClient();
        var differentCredential = fixture.Client.Settings with { CredentialId = Guid.NewGuid() };
        var different = new WorkflowOfficeStorageClient(differentCredential, StorageToken, fixture.DirectoryPath, http);
        Assert.Null(different.ReadOfflineJournal());
        fixture.DatabaseStatus = HttpStatusCode.Unauthorized;
        await Assert.ThrowsAsync<WorkflowValidationException>(() => fixture.Client.CreateStore().ReadAsync(token));
        Assert.Null(fixture.Client.ReadOfflineJournal());
        var file = Assert.Single(Directory.GetFiles(fixture.DirectoryPath, ".office-journal-*.json"));
        await using (var invalid = File.Open(file, FileMode.Open, FileAccess.Write))
        {
            invalid.SetLength(0);
            await invalid.WriteAsync("{"u8.ToArray(), token);
        }
        var restart = new WorkflowOfficeStorageClient(fixture.Client.Settings, StorageToken, fixture.DirectoryPath, http);
        Assert.Null(restart.ReadOfflineJournal());
    }
}
