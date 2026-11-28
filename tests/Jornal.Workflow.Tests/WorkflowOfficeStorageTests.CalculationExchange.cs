using System.IO.Compression;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Jornal.Workflow.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed partial class WorkflowOfficeStorageTests
{
    [Theory]
    [InlineData("новый файл")]
    [InlineData("тот же файл")]
    [InlineData("офис изменил заявку")]
    [InlineData("офис изменил спецификацию")]
    [InlineData("офис изменил тариф")]
    [InlineData("другая база")]
    [InlineData("повреждённый пакет")]
    [InlineData("ответ о записи потерян")]
    [InlineData("офис изменил заявку после подготовки пакета")]
    [InlineData("офис записал файл после сохранения расчёта")]
    [InlineData("сервер принял изменение относительно принятой спецификации")]
    [InlineData("сервер оставил для сверки изменения обеих сторон")]
    [InlineData("сервер не распознал спецификацию")]
    [InlineData("сервер встретил более свежий расчёт при замене файла")]
    [InlineData("сервер встретил новый БЛЦБ при замене файла")]
    [InlineData("сервер перестроил изменённый временный файл")]
    public async Task CalculationExchangeWorksWithoutConnectionAndPreservesOfficeChanges(string scenario)
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var project = await fixture.AddAsync();
        var inbox = scenario.StartsWith("сервер", StringComparison.Ordinal);
        if (inbox)
        {
            await ProjectFixture.WriteAsync(project);
            await fixture.AcceptAsync();
        }
        var token = TestContext.Current.CancellationToken;
        var book = await fixture.Store.ReadAsync(token);
        var order = Assert.Single(book.Orders);
        var office = new MemoryMdb { Book = book };
        var other = new MemoryMdb { Book = book };
        var target = scenario == "другая база" ? other : office;
        var home = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "home")).FullName;
        var exchange = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "Обмен расчётами")).FullName;
        var reportPath = Path.Combine(home, Path.GetFileName(ProjectFixture.ReportPath(project)));
        var reportText = ProjectFixture.Html(project, 12000);
        await File.WriteAllTextAsync(reportPath, reportText, token);
        var snapshot = await WorkflowReportFiles.ReadAsync(reportPath, token);
        var parsed = WorkflowViewModel.FromReport(snapshot.Parsed, reportPath, book.Machines, project.Id);
        var rate = book.Rates.Single(rate => rate.MachineId == project.MachineId);
        var parameters = parsed.Parameters() with { Machine = rate.Machine, MachineId = rate.MachineId };
        var rejectedReport = scenario == "сервер не распознал спецификацию";
        if (rejectedReport)
        {
            var bytes = "<html><body>Обычный документ</body></html>"u8.ToArray();
            snapshot = snapshot with { Bytes = bytes, Sha256 = Hash(bytes) };
        }
        var requests = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            requests++;
            throw new HttpRequestException("Нет связи с офисом.");
        }));
        var settings = new WorkflowOfficeStorageSettings(Address, office.Id, Guid.NewGuid(), true);
        var client = new WorkflowOfficeStorageClient(settings, StorageToken, home, http, http);
        var homePreferences = new WorkflowPreferences
        { UseOfficeStorage = true, OfficeStorage = settings, EmployeeId = order.TechnologistId };
        var homePreferencesStore = new WorkflowPreferencesStore(Path.Combine(home, "preferences.json"));
        homePreferencesStore.Save(homePreferences);
        using (client.LockCalculationDraft(order.Id))
            await client.PreserveCalculationDraftAsync(order, order.TechnologistId,
                [new(project.Id, parsed.Number, parsed.DocumentDate(), parameters, rate, project.ImportProfile, snapshot, WorkflowReportCandidate.Values(parsed))], token);
        var remote = new WorkflowViewModel(client.CreateStore(), new(new WorkflowAppUpdater()),
            preferences: homePreferencesStore, initialPreferences: homePreferences, remoteTransport: client);
        try
        {
            await remote.ReloadCommand.ExecuteAsync(null);
            Assert.NotNull(remote.Error);
            Assert.False(remote.ShowCalculationExchange);
            using var form = remote.CreateGeneralSettings();
            form.SelectedSectionIndex = 1;
            form.CalculationExchangeRoot = exchange;
            await form.SaveCommand.ExecuteAsync(null);
            Assert.Null(form.Error);
            Assert.Equal(exchange, homePreferencesStore.Load().CalculationExchangeRoot);
            Assert.True(remote.ShowCalculationExchange);
            var beforeExport = requests;
            await remote.ExportCalculationDraftsAsync(token);
            Assert.Equal(beforeExport, requests);
            Assert.Contains("Пакетов в папке обмена: 1", remote.CalculationExchangeStatus);
            Assert.True(client.HasCalculationDraft(order.Id));
        }
        finally { remote.Detach(); }
        var package = Assert.Single(Directory.GetFiles(exchange));
        Assert.EndsWith(WorkflowCalculationExchange.Extension, package, StringComparison.Ordinal);
        Assert.StartsWith(order.DisplayNumber, Path.GetFileName(package), StringComparison.Ordinal);

        var officeFile = ProjectFixture.ReportPath(project);
        if (inbox)
        {
            var changedInOffice = scenario == "сервер оставил для сверки изменения обеих сторон";
            var newerCalculation = scenario == "сервер встретил более свежий расчёт при замене файла";
            var newerBlcb = scenario == "сервер встретил новый БЛЦБ при замене файла";
            var damageStageOnce = scenario == "сервер перестроил изменённый временный файл";
            var previous = Assert.Single(order.Specifications).Current;
            var needsReview = changedInOffice || rejectedReport || newerCalculation;
            if (changedInOffice) await ProjectFixture.WriteAsync(project, 15000);
            var database = Path.Combine(fixture.DirectoryPath, "office.mdb");
            await File.WriteAllTextAsync(database, "", token);
            File.SetLastWriteTimeUtc(package, DateTime.UtcNow.AddMinutes(-1));
            var updatedOffice = false;
            var access = new CalculationExchangeTestTransport(async (request, cancellation) =>
            {
                if (damageStageOnce && request.CalculationFile is { } file)
                {
                    damageStageOnce = false;
                    var staged = Path.Combine(Path.GetDirectoryName(file.Path)!, ".workflow-reports", $"{file.StagedId:N}.tmp");
                    await File.WriteAllTextAsync(staged, "Временный файл изменился", cancellation);
                }
                if ((newerCalculation || newerBlcb) && !updatedOffice && request.Operation == WorkflowAccessOperation.PublishCalculationFile)
                {
                    updatedOffice = true;
                    var latest = Assert.Single(office.Book.Orders);
                    var editor = new WorkflowMdbStore(database, office, office.Id);
                    await editor.WriteOrderAsync(latest.Id, latest.Version, current => newerCalculation
                        ? WorkflowChanges.SaveSpecification(current, latest.Id, latest.Version, project.Id,
                            previous.Parameters with { Comment = "Свежий расчёт из офиса" }, previous.DocumentNumber, previous.DocumentDate,
                            latest.TechnologistId, DateTimeOffset.UtcNow, previous.SourcePath, previous.ImportProfile, rate, previous.Report)
                        : WorkflowChanges.UpdateOrder(current, latest.Id, latest.Version, latest.ManagerId, latest.TechnologistId,
                            "БЛЦБ-123456", latest.TechnologistId, DateTimeOffset.UtcNow), cancellation);
                }
                return await office.SendAsync(request, cancellation);
            });
            var worker = new OfficeCalculationInbox(exchange, database, book.ProjectRoot!, access,
                NullLogger<OfficeCalculationInbox>.Instance, new());
            await worker.ProcessAvailableAsync(token);
            if (newerCalculation || newerBlcb || scenario == "сервер перестроил изменённый временный файл")
            {
                Assert.True(File.Exists(package));
                Assert.Equal(previous.Report!.Sha256, Hash(await File.ReadAllBytesAsync(officeFile, token)));
            }
            await worker.ProcessAvailableAsync(token);
            Assert.False(File.Exists(package));
            var moved = Path.Combine(exchange, needsReview ? "На проверку" : "Принятые", Path.GetFileName(package));
            Assert.True(File.Exists(moved));
            if (rejectedReport)
            {
                var explanation = await File.ReadAllTextAsync(moved + ".txt", token);
                Assert.Contains("Не удалось распознать спецификацию", explanation);
                Assert.Contains(Path.GetFileName(officeFile), explanation);
            }
            else if (newerCalculation)
            {
                var actual = Assert.Single(Assert.Single(office.Book.Orders).Specifications).Current;
                Assert.Equal("Свежий расчёт из офиса", actual.Parameters.Comment);
                Assert.Equal(previous.Report!.Sha256, actual.Report!.Sha256);
                Assert.Equal(actual.Report.Sha256, Hash(await File.ReadAllBytesAsync(officeFile, token)));
                Assert.Contains("изменилась в офисе", await File.ReadAllTextAsync(moved + ".txt", token));
            }
            else Assert.Contains(changedInOffice ? "15000" : "12000", await File.ReadAllTextAsync(officeFile, token));
            if (newerBlcb) Assert.Equal("БЛЦБ-123456", Assert.Single(office.Book.Orders).Blcb);
            var expectedWrites = newerCalculation || newerBlcb ? 2 : needsReview ? 0 : 1;
            Assert.Equal(expectedWrites, office.Requests.Count(request => request.Operation == WorkflowAccessOperation.Apply));
            await worker.ProcessAvailableAsync(token);
            Assert.Equal(expectedWrites, office.Requests.Count(request => request.Operation == WorkflowAccessOperation.Apply));
            return;
        }
        if (scenario is "тот же файл" or "офис изменил спецификацию")
            await File.WriteAllTextAsync(officeFile, scenario == "тот же файл" ? reportText : ProjectFixture.Html(project, 15000), token);
        if (scenario == "офис изменил заявку")
            office.Book = book with { Orders = [order with { Version = order.Version + 1, Blcb = "БЛЦБ-010121" }], Version = book.Version + 1 };
        if (scenario == "офис изменил тариф")
        {
            office.Book = WorkflowChanges.SetRate(book, rate with { PricePerMeter = rate.PricePerMeter + 10 });
            office.BumpCatalogVersion();
        }
        if (scenario == "повреждённый пакет")
        {
            using var zip = ZipFile.Open(package, ZipArchiveMode.Update);
            var report = Assert.Single(zip.Entries, entry => entry.Name.EndsWith(".html", StringComparison.Ordinal));
            await using var stream = report.Open();
            stream.Position = 0;
            stream.WriteByte((byte)'?');
        }
        var reads = 0;
        var transport = new CalculationExchangeTestTransport(async (request, cancellation) =>
        {
            if (scenario == "офис изменил заявку после подготовки пакета" &&
                request.Operation == WorkflowAccessOperation.ReadOrders && ++reads == 2)
                target.Book = book with { Orders = [order with { Version = order.Version + 1, Blcb = "БЛЦБ-010121" }], Version = book.Version + 1 };
            var result = await target.SendAsync(request, cancellation);
            if (scenario == "офис записал файл после сохранения расчёта" && request.Operation == WorkflowAccessOperation.Apply)
                await File.WriteAllTextAsync(officeFile, ProjectFixture.Html(project, 15000), cancellation);
            return result;
        });
        var store = new WorkflowMdbStore(Path.Combine(fixture.DirectoryPath, "office.mdb"), transport, target.Id);
        var localPreferences = new WorkflowPreferences { CalculationExchangeRoot = exchange, EmployeeId = order.TechnologistId,
            DatabasePath = store.FilePath, DatabaseId = target.Id };
        var localPreferencesStore = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "office-preferences.json"));
        localPreferencesStore.Save(localPreferences);
        var local = new WorkflowViewModel(store, new(new WorkflowAppUpdater()), preferences: localPreferencesStore, initialPreferences: localPreferences);
        try
        {
            await local.ReloadCommand.ExecuteAsync(null);
            Assert.Null(local.Error);
            target.LoseNextWriteReply = scenario == "ответ о записи потерян";
            await local.ImportCalculationPackageAsync(package, token);
            var beforeRetry = target.Requests.Count(request => request.Operation == WorkflowAccessOperation.Apply);
            if (scenario is "офис записал файл после сохранения расчёта")
            {
                Assert.Equal(1, beforeRetry);
                var accepted = Assert.Single(Assert.Single(target.Book.Orders).Specifications).Current.Report;
                Assert.Equal(snapshot.Sha256, accepted?.Sha256);
                Assert.Contains("Расчёт записан", local.Error);
                Assert.Contains("в офисе отличается", local.Error);
                Assert.Contains("15000", await File.ReadAllTextAsync(officeFile, token));
            }
            else if (scenario is "новый файл" or "тот же файл" or "ответ о записи потерян")
            {
                Assert.Equal(1, beforeRetry);
                if (scenario == "ответ о записи потерян")
                {
                    Assert.NotNull(local.Error);
                    Assert.False(File.Exists(officeFile));
                }
                else Assert.Contains("принят в офисную базу", local.CalculationExchangeStatus);
                var record = Assert.Single(Assert.Single(target.Book.Orders).Specifications).Current;
                Assert.Equal(snapshot.Sha256, record.Report?.Sha256);
                Assert.Equal(officeFile, record.SourcePath);
                await local.ImportCalculationPackageAsync(package, token);
                Assert.Null(local.Error);
                Assert.Contains("уже в офисной базе", local.CalculationExchangeStatus);
                Assert.Equal(reportText, await File.ReadAllTextAsync(officeFile, token));
            }
            else
            {
                Assert.Equal(0, beforeRetry);
                Assert.Empty(Assert.Single(target.Book.Orders).Specifications);
                Assert.Contains(scenario switch
                {
                    "офис изменил заявку" => "изменилась в офисе",
                    "офис изменил заявку после подготовки пакета" => "Данные изменились",
                    "офис изменил спецификацию" => "в офисе отличается",
                    "офис изменил тариф" => "Тариф проекта",
                    "другая база" => "другой базы",
                    _ => "повреждён"
                }, local.Error);
                if (scenario is "офис изменил заявку" or "офис изменил заявку после подготовки пакета") Assert.Equal("БЛЦБ-010121", Assert.Single(target.Book.Orders).Blcb);
                if (scenario == "офис изменил спецификацию") Assert.Contains("15000", await File.ReadAllTextAsync(officeFile, token));
                else Assert.False(File.Exists(officeFile));
            }
            Assert.Equal(beforeRetry, target.Requests.Count(request => request.Operation == WorkflowAccessOperation.Apply));
            Assert.True(File.Exists(package));
        }
        finally { local.Detach(); }
    }

    [Fact]
    public async Task PublicationRetainsThePreviousFileWhenTheWorkingCopyChangesAfterReplacement()
    {
        using var fixture = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "Projects")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "Project")).FullName;
        var archive = Directory.CreateDirectory(Path.Combine(folder, ".workflow-reports")).FullName;
        var file = Path.Combine(folder, "Specification.html");
        var stagedId = Guid.NewGuid();
        var staged = Path.Combine(archive, $"{stagedId:N}.tmp");
        const string previous = "Последняя офисная версия";
        const string incoming = "Домашняя спецификация";
        await File.WriteAllTextAsync(file, previous, token);
        await File.WriteAllTextAsync(staged, incoming, token);
        var (_, beforeHash) = await WorkflowOfficeProjectPaths.FingerprintAsync(file, token);
        var (_, stagedHash) = await WorkflowOfficeProjectPaths.FingerprintAsync(staged, token);
        var publication = new WorkflowCalculationFilePublication(Guid.NewGuid(), 2, root, file, stagedId, stagedHash, beforeHash);

        var error = Assert.Throws<WorkflowValidationException>(() => WorkflowCalculationExchange.PublishStaged(publication, replace =>
        {
            replace();
            File.WriteAllText(file, "Рабочий файл успел измениться");
        }));

        var backup = staged + ".previous";
        Assert.Contains(backup, error.Message);
        Assert.Equal(previous, await File.ReadAllTextAsync(backup, token));
    }

    private sealed class CalculationExchangeTestTransport(
        Func<WorkflowAccessRequest, CancellationToken, Task<WorkflowAccessResponse>> send) : IWorkflowAccessTransport
    {
        public Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken token = default) => send(request, token);
    }
}
