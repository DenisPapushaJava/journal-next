using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed partial class WorkflowOfficeStorageTests
{
    [Theory]
    [InlineData("запись не началась")]
    [InlineData("ответ потерян")]
    [InlineData("новая запись в офисе")]
    [InlineData("новый файл в офисе")]
    [InlineData("сервер временно занят при чтении")]
    public async Task CalculationDraftSurvivesRestartAndChecksOfficeBeforeWritingAgain(string scenario)
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var project = await fixture.AddAsync();
        var token = TestContext.Current.CancellationToken;
        await ProjectFixture.WriteAsync(project);
        var officeFile = ProjectFixture.ReportPath(project);
        var order = (await fixture.Store.ReadAsync(token)).Orders.Single();
        var office = new MemoryMdb { Book = await fixture.Store.ReadAsync(token) };
        var settings = new WorkflowOfficeStorageSettings(Address, office.Id, Guid.NewGuid(), true);
        var profile = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "home")).FullName;
        var prefs = new WorkflowPreferences { UseOfficeStorage = true, OfficeStorage = settings, EmployeeId = order.TechnologistId };
        var preferences = new WorkflowPreferencesStore(Path.Combine(profile, "preferences.json"));
        preferences.Save(prefs);
        var failApply = true;
        var busyOnce = false;
        var applies = 0;
        var uploads = 0;
        using var http = new HttpClient(new Handler(async (request, cancellation) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/database", StringComparison.Ordinal))
            {
                var access = (await request.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
                if (busyOnce && access.Operation == WorkflowAccessOperation.ReadOrders)
                {
                    busyOnce = false;
                    return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { conflict = true }) };
                }
                if (access.Operation == WorkflowAccessOperation.Apply)
                {
                    applies++;
                    if (failApply)
                    {
                        failApply = false;
                        if (scenario == "ответ потерян") await office.SendAsync(access, cancellation);
                        throw new HttpRequestException("Соединение оборвалось после отправки спецификации.");
                    }
                }
                return Reply(await office.SendAsync(access, cancellation));
            }
            if (path.EndsWith("/capabilities", StringComparison.Ordinal))
                return Reply(new { guardedCalculation = true });
            if (path.EndsWith("/calculation", StringComparison.Ordinal))
            {
                var preparation = (await request.Content!.ReadFromJsonAsync<WorkflowOfficeCalculationPreparation>(cancellation))!;
                Assert.Equal(office.Book.Orders.Single().Version, preparation.OrderVersion);
                var submitted = Assert.IsType<WorkflowOfficeReportInput>(preparation.Report);
                var report = await WorkflowReportFiles.ArchiveWorkingReportAsync(officeFile, submitted.Sha256, submitted.Baseline, cancellation);
                return Reply(new WorkflowOfficePreparedCalculation(officeFile, report));
            }
            if (path.EndsWith("/files", StringComparison.Ordinal))
            {
                var data = await File.ReadAllBytesAsync(officeFile, cancellation);
                return Reply(new
                {
                    directories = Array.Empty<string>(),
                    files = new[] { new { path = Path.GetFileName(officeFile), length = data.Length, sha256 = Hash(data) } }
                });
            }
            Assert.EndsWith("/file", path);
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(officeFile, cancellation)) };
            Assert.Equal(HttpMethod.Put, request.Method);
            var before = await File.ReadAllBytesAsync(officeFile, cancellation);
            Assert.Equal($"\"{Hash(before)}\"", Assert.Single(request.Headers.IfMatch).Tag);
            var incoming = await request.Content!.ReadAsByteArrayAsync(cancellation);
            await File.WriteAllBytesAsync(officeFile, incoming, cancellation);
            uploads++;
            return Reply(new { path = Path.GetFileName(officeFile), sha256 = Hash(incoming) });
        }));
        WorkflowOfficeStorageClient NewClient() => new(settings, StorageToken, profile, http, http);
        async Task<WorkflowViewModel> OpenOrderAsync(WorkflowOfficeStorageClient client)
        {
            var model = new WorkflowViewModel(client.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences,
                initialPreferences: prefs, remoteTransport: client);
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.Null(model.Error);
            model.SelectedOrder = Assert.Single(model.Orders);
            model.OpenOrderCommand.Execute(null);
            return model;
        }
        var firstClient = NewClient();
        var local = await firstClient.OpenWorkingCopyAsync(order.Id, project.Id, token);
        await File.WriteAllTextAsync(Path.Combine(local.Folder, Path.GetFileName(officeFile)), ProjectFixture.Html(project, 12000), token);
        var first = await OpenOrderAsync(firstClient);
        CuttingParameters submittedParameters;
        try
        {
            var attempt = Assert.IsType<WorkflowReportReviewViewModel>(await first.CalculateOrderAsync(token));
            Assert.Null(attempt.Error);
            Assert.True(attempt.QueuedForOffice);
            Assert.Contains("на этом компьютере", first.CalculationExchangeStatus);
            Assert.Equal(1, applies);
            Assert.Equal(1, uploads);
            Assert.True(firstClient.HasCalculationDraft(order.Id));
            submittedParameters = Assert.Single(Assert.IsType<WorkflowOfficeCalculationDraft>(firstClient.ReadCalculationDraft(order.Id)).Entries).Parameters;
        }
        finally { first.Detach(); }
        var cleanup = await firstClient.PreviewCleanupAsync([order], new HashSet<Guid>(), null, null, token);
        Assert.Empty(cleanup.Copies);
        Assert.Contains("незавершённый расчёт", Assert.Single(cleanup.Skipped));

        var newProjectId = Guid.NewGuid();
        if (scenario == "новая запись в офисе")
        {
            var latest = office.Book.Orders.Single();
            var laterProject = project with { Id = newProjectId, ItemId = Guid.NewGuid(), Name = "Новый офисный проект", NativePath = null };
            office.Book = WorkflowProjects.Add(office.Book, latest.Id, latest.Version, laterProject, order.TechnologistId, DateTimeOffset.UtcNow)
                with { Version = office.Book.Version + 1 };
        }
        if (scenario == "новый файл в офисе") await ProjectFixture.WriteAsync(project, 16000);
        var restarted = NewClient();
        var resumed = await OpenOrderAsync(restarted);
        try
        {
            Assert.Contains("незавершённый расчёт", resumed.CalculationNotice);
            var now = DateTimeOffset.UtcNow;
            busyOnce = scenario == "сервер временно занят при чтении";
            await resumed.SynchronizeOfficeOrdersAsync(now, token);
            if (scenario == "сервер временно занят при чтении")
            {
                Assert.False(busyOnce);
                Assert.True(restarted.HasCalculationDraft(order.Id));
                Assert.Null(resumed.CalculationExchangeStatus);
                Assert.Equal(1, applies);
                await resumed.SynchronizeOfficeOrdersAsync(now.AddSeconds(11), token);
            }
            Assert.Equal(1, uploads);
            if (scenario.StartsWith("нов", StringComparison.Ordinal))
            {
                Assert.Equal(1, applies);
                Assert.True(restarted.HasCalculationDraft(order.Id));
                Assert.Contains("требуют сверки", resumed.CalculationExchangeStatus);
                Assert.Null(await resumed.CalculateOrderAsync(token));
                Assert.Equal(CalculationTarget.OfficeDraft, resumed.GoToCalculationProblem());
                var archive = Assert.IsType<string>(resumed.OfficeCalculationConflictFolder);
                Assert.True(await resumed.ArchiveConflictingOfficeCalculationAsync(token));
                Assert.False(restarted.HasCalculationDraft(order.Id));
                Assert.True(File.Exists(Path.Combine(archive, "draft.json")));
                var savedSource = Assert.Single(Directory.EnumerateFiles(archive, "*.html", SearchOption.AllDirectories));
                Assert.Contains("12000", await File.ReadAllTextAsync(savedSource, token));
                if (scenario == "новая запись в офисе")
                    Assert.Contains(office.Book.Orders.Single().Projects, later => later.Id == newProjectId);
                if (scenario == "новый файл в офисе") Assert.Contains("16000", await File.ReadAllTextAsync(officeFile, token));
            }
            else
            {
                Assert.Null(resumed.Error);
                Assert.Contains("подтверждены", resumed.CalculationExchangeStatus);
                Assert.Equal(scenario == "ответ потерян" ? 1 : 2, applies);
                Assert.False(restarted.HasCalculationDraft(order.Id));
                Assert.True(submittedParameters.SameInputs(office.Book.Orders.Single().Specifications.Single().Current.Parameters));
            }
        }
        finally { resumed.Detach(); }
    }

    private static HttpResponseMessage Reply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
