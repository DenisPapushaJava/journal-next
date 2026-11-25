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
    private const string Address = "http://10.0.0.2:60913/";
    private const string StorageToken = "storage.example";

    [Fact]
    public async Task InterruptedPublicationRequiresMatchingReceiptAndAllowsLaterPathChange()
    {
        using var fixture = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.DirectoryPath, "Test.mdb");
        var database = Guid.NewGuid();
        var order = Guid.NewGuid();
        var writer = Guid.NewGuid();
        var reader = Guid.NewGuid();
        var project = Guid.NewGuid();
        var originalPath = "Плазма/Менеджер/Заявка/Проект";
        var changedPath = "Unimach&HL/Менеджер/Заявка/Проект";
        var first = new string('A', 64);
        var second = new string('B', 64);
        using (var publication = WorkflowShopPublicationLock.Acquire(path, database, order, writer))
            await publication.UpdateAsync(new(ShopPublicationStep.Reserve, project, originalPath, null, first), token);
        using (var publication = WorkflowShopPublicationLock.Acquire(path, database, order, writer))
            await publication.UpdateAsync(new(ShopPublicationStep.Check, project, originalPath, null), token);
        using (var publication = WorkflowShopPublicationLock.Acquire(path, database, order, reader))
        {
            await Assert.ThrowsAsync<WorkflowValidationException>(() =>
                publication.UpdateAsync(new(ShopPublicationStep.Check, project, originalPath, null), token));
            await Assert.ThrowsAsync<WorkflowValidationException>(() =>
                publication.UpdateAsync(new(ShopPublicationStep.Reserve, project, changedPath, null, second), token));
            await publication.UpdateAsync(new(ShopPublicationStep.Check, project, originalPath, first), token);
            await publication.UpdateAsync(new(ShopPublicationStep.Confirm, project, originalPath, first), token);
            await publication.UpdateAsync(new(ShopPublicationStep.Check, project, changedPath, null), token);
            await publication.UpdateAsync(new(ShopPublicationStep.Reserve, project, changedPath, null, second), token);
            await publication.UpdateAsync(new(ShopPublicationStep.Confirm, project, changedPath, second), token);
        }
        using var retry = WorkflowShopPublicationLock.Acquire(path, database, order, writer);
        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            retry.UpdateAsync(new(ShopPublicationStep.Reserve, project, originalPath, null, second), token));
    }

    [Theory]
    [InlineData("готово")]
    [InlineData("изменено в офисе")]
    [InlineData("ответ потерян")]
    [InlineData("локальная правка")]
    [InlineData("отправляет локальную правку")]
    [InlineData("офисная правка до отправки")]
    [InlineData("офисная правка во время отправки")]
    [InlineData("офисная правка после загрузки")]
    [InlineData("потерян ответ загрузки")]
    [InlineData("домашняя карта заменена посторонней")]
    [InlineData("обе одноимённые карты посторонние")]
    [InlineData("домашняя правка после подтверждения")]
    [InlineData("папка не синхронизирована")]
    [InlineData("занят посторонний PDF")]
    [InlineData("разблокирован посторонний PDF")]
    [InlineData("разблокирована актуальная карта")]
    [InlineData("изменён посторонний PDF")]
    [InlineData("посторонний PDF стал картой")]
    [InlineData("изменён готовый комплект")]
    [InlineData("изменён после подтверждения")]
    [InlineData("повтор без замены")]
    public async Task RemoteShopTransferStagesOfficeFilesChecksAgainAndRecordsThroughServer(string scenario)
    {
        using var f = new MapSigningFixture();
        using var home = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        var map = f.Create();
        var original = await File.ReadAllBytesAsync(map, token);
        var program = Path.Combine(f.Root, "Лист1.cnc");
        await File.WriteAllTextAsync(program, "УП", token);
        await File.WriteAllTextAsync(Path.Combine(f.Root, "Чертёж.dxf"), "Лишний файл", token);
        var other = Path.Combine(f.Root, "Чертёж.pdf");
        if (scenario is "изменён посторонний PDF" or "посторонний PDF стал картой" or "разблокирован посторонний PDF" or "обе одноимённые карты посторонние")
            f.Create(name: "Чертёж.pdf", native: "Чужой.rcam", cnc: "Другой.cnc");
        if (scenario == "разблокирована актуальная карта") File.Copy(map, other);
        using var locked = scenario is "занят посторонний PDF" or "разблокирован посторонний PDF" or "разблокирована актуальная карта" ?
            new FileStream(other, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None) : null;
        var bending = Directory.CreateDirectory(Path.Combine(f.Root, "ГИБКА", "Лист 1"));
        await File.WriteAllTextAsync(Path.Combine(bending.FullName, "Эскиз.txt"), "Работа на гибку", token);
        var spec = Assert.Single(f.Order.Specifications);
        f.Book = f.Book with
        {
            Employees = [f.Book.Employees.Single() with { IsTechnologist = true }],
            Orders = [f.Order with { Specifications = [spec with
                { Current = spec.Current with { Parameters = spec.Current.Parameters with { HasBending = true } } }] }]
        };
        var order = f.Order;
        if (scenario == "папка не синхронизирована")
        {
            f.Book = WorkflowChanges.RecordShopHandoff(f.Book, order.Id, order.Version, order.TechnologistId, DateTimeOffset.UtcNow);
            order = f.Order;
        }
        var machine = f.Book.Machines.Single(m => m.Id == f.Project.MachineId);
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var lease = Guid.NewGuid().ToString("D");
        var journalPath = Path.Combine(home.DirectoryPath, "Test.mdb");
        var markAttempts = 0;
        var clipboardWrites = 0;
        var leaseRequests = 0;
        var otherDownloads = 0;
        var uploads = new List<string>();
        var root = Directory.CreateDirectory(Path.Combine(home.DirectoryPath, "Цех")).FullName;
        var destination = WorkflowShopFolderPaths.Plan(root, order, f.Book.Employees, f.Book.Customers, f.Book.Machines).Single().Path;
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            if (path.EndsWith("/capabilities", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { guardedCalculation = true, shopPublicationJournal = true }) };
            if (path.EndsWith("/shop-publication", StringComparison.Ordinal))
            {
                leaseRequests++;
                Assert.Contains("&owner=" + owner.ToString("D"), message.RequestUri.Query);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
                response.Headers.Add(WorkflowShopPublicationLock.LeaseHeader, lease);
                return response;
            }
            if (path.EndsWith("/shop-publication/project", StringComparison.Ordinal))
            {
                Assert.Equal(lease, Assert.Single(message.Headers.GetValues(WorkflowShopPublicationLock.LeaseHeader)));
                var change = (await message.Content!.ReadFromJsonAsync<ShopPublicationChange>(cancellation))!;
                using var publication = WorkflowShopPublicationLock.Acquire(journalPath, id, order.Id, owner);
                try { await publication.UpdateAsync(change, cancellation); }
                catch (WorkflowValidationException ex)
                { return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { error = ex.Message }) }; }
                if (scenario == "изменён после подтверждения" && change.Step == ShopPublicationStep.Confirm)
                {
                    void ChangePublishedProgram() => File.WriteAllText(Path.Combine(destination, "Лист1.cnc"), "Изменено после Confirm");
                    if (OperatingSystem.IsWindows()) Assert.ThrowsAny<IOException>(ChangePublishedProgram);
                    else ChangePublishedProgram();
                }
                return new(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/shop-sources", StringComparison.Ordinal))
            {
                if (message.Headers.TryGetValues(WorkflowShopPublicationLock.LeaseHeader, out var keys)) Assert.Equal(lease, Assert.Single(keys));
                var (tree, unavailable) = await WorkflowOfficeProjectPaths.ShopManifestAsync(f.Root, f.Project, machine, f.Order.Specifications.Single().Current, cancellation);
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { tree.Directories, UnavailableFiles = unavailable,
                        Files = tree.Files.Select(file => new { Path = file.RelativePath, file.Length, file.Sha256 }) })
                };
            }
            if (path.EndsWith("/files", StringComparison.Ordinal))
            {
                var tree = await WorkflowOfficeProjectPaths.ManifestAsync(f.Root, cancellation);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    tree.Directories,
                    Files = tree.Files.Select(file => new { Path = file.RelativePath.Replace('\\', '/'), file.Length, file.Sha256 })
                }) };
            }
            if (path.EndsWith("/file", StringComparison.Ordinal))
            {
                var file = Uri.UnescapeDataString(message.RequestUri.Query["?path=".Length..]);
                if (file == "Чертёж.pdf") otherDownloads++;
                Assert.NotEqual(".dxf", Path.GetExtension(file));
                if (message.Method == HttpMethod.Put)
                {
                    uploads.Add(file);
                    Assert.Equal("Лист1.cnc", file);
                    Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(program, cancellation))),
                        Assert.Single(message.Headers.IfMatch).Tag.Trim('"'));
                    if (scenario == "офисная правка во время отправки")
                    {
                        await File.WriteAllTextAsync(program, "Офисная версия", cancellation);
                        return new(HttpStatusCode.PreconditionFailed);
                    }
                    var uploaded = await message.Content!.ReadAsByteArrayAsync(cancellation);
                    await File.WriteAllBytesAsync(program, uploaded, cancellation);
                    if (scenario == "потерян ответ загрузки") throw new HttpRequestException("Потерян ответ после записи");
                    if (scenario == "офисная правка после загрузки") await File.WriteAllTextAsync(program, "Офисная версия", cancellation);
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { path = file, sha256 = Convert.ToHexString(SHA256.HashData(uploaded)) }) };
                }
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(f.Root, file), cancellation)) };
            }
            Assert.EndsWith("/database", path);
            var request = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            if (request.ShopHandoff is { } mark)
            {
                Assert.Equal(lease, Assert.Single(message.Headers.GetValues(WorkflowShopPublicationLock.LeaseHeader)));
                f.Book = WorkflowChanges.RecordShopHandoff(f.Book, mark.OrderId, mark.ExpectedOrderVersion, mark.ActorId, mark.At);
                if (++markAttempts == 1 && scenario == "ответ потерян")
                    return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { outcomeUnknown = true }) };
            }
            return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new WorkflowAccessResponse(1, id, Catalog: WorkflowCatalog.From(f.Book), Orders: f.Book.Orders)) };
        }));
        var client = new WorkflowOfficeStorageClient(new(Address, id, Guid.NewGuid(), true), StorageToken, home.DirectoryPath, http, http);
        var store = client.CreateStore();
        var workingCopy = Directory.CreateDirectory(Path.Combine(home.DirectoryPath, "office-projects", id.ToString("N"), f.Project.Id.ToString("N"))).FullName;
        var baselineHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(program, token)));
        var baseline = new Dictionary<string, string> { ["Лист1.cnc"] = baselineHash };
        if (scenario == "домашняя карта заменена посторонней") baseline[Path.GetFileName(map)] = Convert.ToHexString(SHA256.HashData(original));
        if (scenario == "обе одноимённые карты посторонние")
            baseline[Path.GetFileName(other)] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(other, token)));
        await File.WriteAllTextAsync(Path.Combine(workingCopy, ".office-copy.json"),
            System.Text.Json.JsonSerializer.Serialize(baseline), token);
        File.Copy(program, Path.Combine(workingCopy, "Лист1.cnc"));
        if (scenario != "потерян ответ загрузки")
            await File.WriteAllTextAsync(Path.Combine(workingCopy, "Домашний.dxf"), "Лишняя правка вне комплекта", token);
        var homeEdit = scenario is "локальная правка" or "отправляет локальную правку" or "офисная правка до отправки" or
            "офисная правка во время отправки" or "офисная правка после загрузки" or "потерян ответ загрузки" or "домашняя правка после подтверждения";
        if (homeEdit)
            await File.WriteAllTextAsync(Path.Combine(workingCopy, "Лист1.cnc"), "Правка технолога", token);
        if (scenario == "отправляет локальную правку")
        {
            var foreign = f.Create(name: "Чужая карта.pdf", native: "Чужой.rcam", cnc: "Другой.cnc");
            File.Move(foreign, Path.Combine(workingCopy, Path.GetFileName(foreign)));
        }
        if (scenario is "домашняя карта заменена посторонней" or "обе одноимённые карты посторонние")
        {
            var foreign = f.Create(name: "Домашняя карта.pdf", native: "Чужой-2.rcam", cnc: "Другой-2.cnc");
            File.Move(foreign, Path.Combine(workingCopy,
                Path.GetFileName(scenario == "домашняя карта заменена посторонней" ? map : other)));
        }
        if (scenario == "офисная правка до отправки") await File.WriteAllTextAsync(program, "Офисная версия", token);
        WorkflowShopTransferViewModel NewModel() => new(root, id.ToString("D"), order, f.Book, _ => Task.CompletedTask,
            _ => { clipboardWrites++; return Task.CompletedTask; },
            transferred: results =>
            {
                if (scenario == "изменён готовый комплект" && results.Any(r => r.Success))
                {
                    File.WriteAllText(Path.Combine(destination, "Лист1.cnc"), "Файл обновился после копирования");
                }
            },
            recordHandoff: (at, ct) => store.RecordShopHandoffAsync(order.Id, order.Version, order.TechnologistId, at, ct))
        {
            PrepareOfficeSources = (approved, progress, ct) => client.PrepareShopSourcesAsync(order, f.Book, null, progress, ct, approved),
            OpenPublication = ct => client.OpenShopPublicationAsync(order, owner, ct)
        };
        var model = NewModel();
        try
        {
            await model.RefreshAsync();
            if (scenario == "домашняя карта заменена посторонней")
            {
                Assert.Contains(Path.GetFileName(map), model.Error);
                Assert.Empty(uploads);
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
                Assert.False(model.CanCopy);
                Assert.Equal(0, markAttempts);
                return;
            }
            if (homeEdit)
            {
                Assert.True(model.CanActOnOfficeReview, model.Error);
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
                Assert.Equal(0, leaseRequests);
                Assert.Empty(uploads);
                var review = Assert.IsType<WorkflowOfficeShopReview>(model.OfficeReview);
                if (scenario == "офисная правка до отправки")
                {
                    Assert.Empty(review.Files);
                    Assert.Equal("Лист1.cnc", review.Conflict?.Path);
                    var conflict = review.Conflict!.ToConflict();
                    await File.WriteAllTextAsync(program, "Офисная версия 2", token);
                    var comparison = await client.CompareWorkingFileAsync(order.Id, f.Project.Id, conflict, token, refreshVersions: true);
                    Assert.Equal("Офисная версия 2", await File.ReadAllTextAsync(comparison.OfficeFile!, token));
                    var current = new WorkflowOfficeFileConflict(conflict.Path, comparison.HomeHash, comparison.OfficeHash);
                    await File.WriteAllTextAsync(program, "Офисная версия 3", token);
                    await Assert.ThrowsAsync<WorkflowValidationException>(() =>
                        client.ChooseWorkingFileAsync(order.Id, f.Project.Id, current, comparison, useOffice: false, token));
                    comparison = await client.CompareWorkingFileAsync(order.Id, f.Project.Id, conflict, token, refreshVersions: true);
                    current = new(conflict.Path, comparison.HomeHash, comparison.OfficeHash);
                    await client.ChooseWorkingFileAsync(order.Id, f.Project.Id, current, comparison, useOffice: false, token);
                    await model.ContinueOfficeReviewAsync(review);
                    review = Assert.IsType<WorkflowOfficeShopReview>(model.OfficeReview);
                    Assert.Empty(uploads);
                    Assert.Equal("Офисная версия 3", await File.ReadAllTextAsync(program, token));
                }
                Assert.Null(review.Conflict);
                Assert.Equal("Лист1.cnc", Assert.Single(review.Files).Path);
                if (scenario == "локальная правка") return;
                if (scenario == "домашняя правка после подтверждения")
                    await File.WriteAllTextAsync(Path.Combine(workingCopy, "Лист1.cnc"), "Новая домашняя версия", token);
                await model.ContinueOfficeReviewAsync(review);
                if (scenario == "домашняя правка после подтверждения")
                {
                    Assert.Empty(uploads);
                    Assert.NotEqual(Assert.Single(review.Files).HomeHash, Assert.Single(model.OfficeReview!.Files).HomeHash);
                    Assert.False(model.CanCopy);
                    return;
                }
                Assert.Equal("Лист1.cnc", Assert.Single(uploads));
                if (scenario is "офисная правка во время отправки" or "офисная правка после загрузки")
                {
                    Assert.Equal("Лист1.cnc", model.OfficeReview?.Conflict?.Path);
                    Assert.Equal("Офисная версия", await File.ReadAllTextAsync(program, token));
                    Assert.Equal("Правка технолога", await File.ReadAllTextAsync(Path.Combine(workingCopy, "Лист1.cnc"), token));
                    Assert.False(model.CanCopy);
                    Assert.Equal(0, markAttempts);
                    Assert.False(model.TextCopied);
                    if (scenario == "офисная правка во время отправки") return;
                    var newReview = Assert.IsType<WorkflowOfficeShopReview>(model.OfficeReview);
                    var conflict = newReview.Conflict!.ToConflict();
                    var comparison = await client.CompareWorkingFileAsync(order.Id, f.Project.Id, conflict, token, refreshVersions: true);
                    await client.ChooseWorkingFileAsync(order.Id, f.Project.Id, conflict, comparison, useOffice: true, token);
                    await model.ContinueOfficeReviewAsync(newReview);
                    Assert.True(model.CanStartAutomatically, model.Error);
                }
                if (scenario == "потерян ответ загрузки")
                {
                    Assert.True(model.CanActOnOfficeReview);
                    Assert.True(await client.HasPendingFilesAsync(f.Project.Id, null, f.Project.NativePath, token));
                    await model.ContinueOfficeReviewAsync(model.OfficeReview!);
                    Assert.True(model.CanStartAutomatically, model.Error);
                    Assert.False(await client.HasPendingFilesAsync(f.Project.Id, null, f.Project.NativePath, token));
                    Assert.Single(uploads);
                }
                Assert.Equal(scenario == "офисная правка после загрузки" ? "Офисная версия" : "Правка технолога",
                    await File.ReadAllTextAsync(program, token));
                Assert.False(File.Exists(Path.Combine(f.Root, "Домашний.dxf")));
                Assert.False(File.Exists(Path.Combine(f.Root, "Чужая карта.pdf")));
            }
            if (scenario == "папка не синхронизирована")
            {
                var existing = Assert.Single(model.Projects);
                var relative = WorkflowShopTransfer.PublicationPath(root, existing.Destination);
                using (var office = WorkflowShopPublicationLock.Acquire(journalPath, id, order.Id, Guid.NewGuid()))
                    await office.UpdateAsync(new(ShopPublicationStep.Reserve, existing.Plan.Project.Id, relative, null, new string('A', 64)), token);
                Assert.Contains("синхронизацию", existing.SelectionHelp);
                existing.Reviewed = true;
                Assert.True(model.CanCopy);
                await model.CopyAsync();
                Assert.Contains("синхронизацию", model.Error);
                Assert.Empty(Directory.EnumerateFileSystemEntries(root));
                Assert.Equal(1, leaseRequests);
                return;
            }
            if (locked is not null)
            {
                var reviewed = Assert.Single(model.Projects);
                Assert.Contains("Чертёж.pdf", reviewed.SelectionHelp);
                Assert.False(model.CanStartAutomatically);
                reviewed.Reviewed = true;
                Assert.True(model.CanCopy, model.Error);
                if (scenario != "занят посторонний PDF") locked.Dispose();
            }
            else Assert.True(model.CanStartAutomatically, model.Error);
            if (scenario == "изменено в офисе") await File.WriteAllTextAsync(program, "Изменено", token);
            if (scenario == "изменён посторонний PDF")
            {
                File.Delete(other);
                f.Create(name: "Чертёж.pdf", native: "Чужой-2.rcam", cnc: "Следующий.cnc");
            }
            if (scenario == "посторонний PDF стал картой") File.Copy(map, other, overwrite: true);
            var otherDownloadsBeforeCopy = otherDownloads;
            await model.CopyAsync();
            var row = Assert.Single(model.Projects);
            Assert.Equal(original, await File.ReadAllBytesAsync(map, token));
            if (scenario is "изменено в офисе" or "посторонний PDF стал картой" or "разблокирована актуальная карта")
            {
                Assert.Contains(scenario == "изменено в офисе" ? "Лист1.cnc" : "Чертёж.pdf", model.Error);
                Assert.False(Directory.Exists(row.Destination));
                Assert.Equal(0, markAttempts);
                Assert.Equal(0, clipboardWrites);
                return;
            }
            Assert.True(model.AllPrepared, model.Error);
            if (scenario == "изменён готовый комплект" || scenario == "изменён после подтверждения" && !OperatingSystem.IsWindows())
            {
                Assert.Contains("готовой папке", model.Error);
                Assert.Equal(0, markAttempts);
                Assert.Equal(0, clipboardWrites);
                await model.CopyTextCommand.ExecuteAsync(null);
                Assert.Equal(0, markAttempts);
                Assert.Equal(0, clipboardWrites);
                return;
            }
            if (scenario is "разблокирован посторонний PDF" or "изменён посторонний PDF")
                Assert.Equal(1, otherDownloads - otherDownloadsBeforeCopy);
            if (scenario == "обе одноимённые карты посторонние")
                Assert.Equal(otherDownloadsBeforeCopy, otherDownloads);
            Assert.Equal(1, markAttempts);
            Assert.True(f.Order.HasShopHandoff);
            Assert.Equal("Работа на гибку", await File.ReadAllTextAsync(Path.Combine(row.Destination, "ГИБКА", "Лист 1", "Эскиз.txt"), token));
            if (scenario == "отправляет локальную правку")
                Assert.Equal("Правка технолога", await File.ReadAllTextAsync(Path.Combine(row.Destination, "Лист1.cnc"), token));
            Assert.Equal(row.DestinationRevision, await WorkflowShopTransfer.DestinationRevisionAsync(row.Plan, id.ToString("D"), order.Id, token));
            using var published = UglyToad.PdfPig.PdfDocument.Open(Path.Combine(row.Destination, Path.GetFileName(map)));
            Assert.Contains("БЛЦБ-009337", published.GetPage(1).Text);
            if (scenario == "ответ потерян")
            {
                Assert.True(store.RequiresRefresh);
                Assert.Equal(0, clipboardWrites);
                await model.CopyTextCommand.ExecuteAsync(null);
                Assert.Equal(1, markAttempts);
                Assert.Equal(0, clipboardWrites);
            }
            else Assert.Equal(1, clipboardWrites);
            if (scenario == "повтор без замены")
            {
                var repeated = NewModel();
                try
                {
                    await repeated.RefreshAsync();
                    Assert.True(repeated.CanStartAutomatically, repeated.Error);
                    await repeated.CopyAsync();
                    Assert.True(repeated.TextCopied, repeated.Error);
                    Assert.Equal(row.DestinationRevision, Assert.Single(repeated.Projects).DestinationRevision);
                }
                finally { repeated.Discard(); }
            }
        }
        finally { model.Discard(); }
    }

    [Fact]
    public async Task ArchivedShopFolderCanBeRepublishedOnlyWithItsVerifiedReceipt()
    {
        using var f = new MapSigningFixture();
        using var home = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        f.Create();
        var program = Path.Combine(f.Root, "Лист1.cnc");
        await File.WriteAllTextAsync(program, "Старая УП", token);
        var root = Path.Combine(home.DirectoryPath, "Цех");
        var db = Path.Combine(home.DirectoryPath, "test.mdb");
        var database = Guid.NewGuid();
        var writer = Guid.NewGuid();
        WorkflowShopTransferViewModel New() => new(root, database.ToString("D"), f.Order, f.Book,
            _ => Task.CompletedTask, _ => Task.CompletedTask, recordHandoff: (_, _) => Task.CompletedTask)
        { OpenPublication = _ => Task.FromResult<IWorkflowShopPublication>(WorkflowShopPublicationLock.Acquire(db, database, f.Order.Id, writer)) };

        var first = New();
        await first.RefreshAsync();
        await first.CopyAsync();
        Assert.True(first.TextCopied, first.Error);
        var destination = Assert.Single(first.Projects).Destination;
        var archive = Path.Combine(home.DirectoryPath, "Архив");
        Directory.Move(destination, archive);
        first.Discard();
        await File.WriteAllTextAsync(program, "Новая УП", token);

        var again = New();
        try
        {
            await again.RefreshAsync();
            await again.CopyAsync();
            Assert.False(again.TextCopied);
            Assert.False(Directory.Exists(destination));
            var row = Assert.Single(again.Projects);
            Assert.True(row.CanChooseArchive);
            again.ChooseArchivedFolder(row, home.DirectoryPath);
            await again.CopyAsync();
            Assert.False(again.TextCopied);
            Assert.False(Directory.Exists(destination));
            again.ChooseArchivedFolder(row, archive);
            await again.CopyAsync();
            Assert.True(again.TextCopied, again.Error);
            Assert.Equal("Старая УП", await File.ReadAllTextAsync(Path.Combine(archive, "Лист1.cnc"), token));
            Assert.Equal("Новая УП", await File.ReadAllTextAsync(Path.Combine(destination, "Лист1.cnc"), token));
        }
        finally { again.Discard(); }
    }

    [Fact]
    public async Task CancellationOfShopFinalizationLeavesPreparedFilesAndCanBeRetried()
    {
        using var f = new MapSigningFixture();
        using var home = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        f.Create();
        await File.WriteAllTextAsync(Path.Combine(f.Root, "Лист1.cnc"), "УП", token);
        var root = Path.Combine(home.DirectoryPath, "Цех");
        var database = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var nextLease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var leases = 0; var recordAttempts = 0; var clipboard = 0;
        var model = new WorkflowShopTransferViewModel(root, database.ToString("D"), f.Order, f.Book, _ => Task.CompletedTask,
            _ => { clipboard++; return Task.CompletedTask; },
            recordHandoff: (_, _) => ++recordAttempts == 1 ? Task.FromException(new IOException()) : Task.CompletedTask)
        {
            OpenPublication = async ct =>
            {
                if (++leases == 2)
                {
                    nextLease.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                return WorkflowShopPublicationLock.Acquire(Path.Combine(home.DirectoryPath, "test.mdb"), database, f.Order.Id, owner);
            }
        };
        try
        {
            await model.RefreshAsync();
            await model.CopyAsync();
            Assert.True(model.AllPrepared, model.Error);
            Assert.False(model.TextCopied);
            var retry = model.CopyTextCommand.ExecuteAsync(null);
            await nextLease.Task.WaitAsync(token);
            Assert.True(model.CanCloseOrCancel);
            model.Cancel();
            await retry.WaitAsync(token);
            Assert.True(model.WasCancelled);
            Assert.False(model.IsBusy);
            Assert.Equal(1, recordAttempts);
            Assert.Equal(0, clipboard);
            Assert.True(Directory.Exists(Assert.Single(model.Projects).Destination));
            await model.CopyTextCommand.ExecuteAsync(null);
            Assert.True(model.TextCopied, model.Error);
            Assert.Equal(2, recordAttempts);
            Assert.Equal(1, clipboard);
        }
        finally { model.Discard(); }
    }

    [Fact]
    public async Task StoredConnectionRequiresTheVerifiedInterfaceAfterRestart()
    {
        using var fixture = new ProjectFixture();
        var id = Guid.NewGuid();
        var legacy = new WorkflowOfficeStorageSettings(Address, id, Guid.NewGuid(), true);
        var route = new WorkflowOfficeRoute(IPAddress.Parse("10.0.0.3"), "Office", "office-adapter");
        new WorkflowOfficeConnection(Address, StorageToken).Save(legacy.CredentialPath(fixture.DirectoryPath));
        Assert.Throws<WorkflowValidationException>(() => WorkflowOfficeStorageClient.Open(legacy, fixture.DirectoryPath));
        var verified = WorkflowOfficeStorageClient.Open(legacy, fixture.DirectoryPath, route).Settings;
        var preferences = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "route-preferences.json"));
        preferences.Save(new() { OfficeStorage = verified, UseOfficeStorage = true });
        var restored = preferences.Load().ActiveOfficeStorage!;
        Assert.Throws<WorkflowValidationException>(() => WorkflowOfficeStorageClient.Open(restored, fixture.DirectoryPath,
            route with { InterfaceId = "other-adapter" }));
        using var http = new HttpClient(new Handler((message, _) =>
        {
            Assert.Equal(StorageToken, message.Headers.Authorization?.Parameter);
            Assert.True(message.Options.TryGetValue(WorkflowOfficeTransport.ExpectedRoute, out var pin));
            Assert.True(pin.Matches(route with { Address = IPAddress.Parse("10.0.0.4") }));
            Assert.False(pin.Matches(route with { InterfaceId = "other-adapter" }));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(new WorkflowAccessResponse(1, DatabaseId: id, Orders: [])) });
        }));
        var client = WorkflowOfficeStorageClient.Open(restored, fixture.DirectoryPath, storageClient: http);
        await client.SendAsync(new(1, WorkflowAccessOperation.Read, ""), TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedConnectionRequiresRefreshOnlyIfAConnectionWasAlreadyOpened(bool opened)
    {
        using var fixture = new ProjectFixture();
        var id = Guid.NewGuid();
        var preferences = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "switch-preferences.json"));
        var remote = new WorkflowOfficeStorageSettings(Address, id, Guid.NewGuid(), true) { ZeroTierInterfaceId = "office-adapter" };
        var saved = new WorkflowPreferences { OfficeStorage = remote, UseOfficeStorage = true };
        preferences.Save(saved);
        using var http = new HttpClient(new Handler(async (message, token) =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(token))!;
            if (request.Operation == WorkflowAccessOperation.Apply)
            {
                if (opened) message.Options.Set(WorkflowOfficeTransport.ConnectedRoute, new(null, null, "office-adapter"));
                throw new HttpRequestException("Connection failed", new WorkflowOfficeConnectException("Проверьте адаптер", true));
            }
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new WorkflowAccessResponse(1, id, Catalog: WorkflowCatalog.From(WorkflowWorkspace.Empty),
                    Orders: [], PageIds: []))
            };
        }));
        var client = new WorkflowOfficeStorageClient(remote, StorageToken, fixture.DirectoryPath, http);
        var store = client.CreateStore();
        var cancellation = TestContext.Current.CancellationToken;
        await store.ReadAsync(cancellation);
        var model = new WorkflowViewModel(store, new(new WorkflowAppUpdater()), preferences: preferences, initialPreferences: saved,
            remoteTransport: client);
        try
        {
            Task SaveAsync() => store.SaveEmployeeProfileAsync(null, "Сотрудник", 0xFF123456, cancellation);
            if (opened) await Assert.ThrowsAsync<WorkflowOutcomeUnknownException>(SaveAsync);
            else Assert.Equal("Проверьте адаптер", (await Assert.ThrowsAsync<WorkflowValidationException>(SaveAsync)).Message);

            Assert.Equal(opened, store.RequiresRefresh);
            var error = await model.ReturnToLocalStorageAsync(cancellation);
            if (opened) Assert.Contains("F5", error);
            else Assert.Null(error);
            Assert.Equal(opened, model.IsRemoteStorage);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public async Task CancellingTheInitialReadLeavesTheLocalSourceAndDoesNotCommit()
    {
        using var fixture = new ProjectFixture();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var preferences = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "switch-preferences.json"));
        var local = new MemoryMdb();
        var localPath = Path.Combine(fixture.DirectoryPath, "local.mdb");
        var saved = new WorkflowPreferences { DatabasePath = localPath, DatabaseId = local.Id };
        preferences.Save(saved);
        var model = new WorkflowViewModel(new WorkflowMdbStore(localPath, local, local.Id), new(new WorkflowAppUpdater()),
            preferences: preferences, databases: local);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            read.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(HttpStatusCode.OK);
        }));
        var remote = new WorkflowOfficeStorageSettings(Address, Guid.NewGuid(), Guid.NewGuid(), true) { ZeroTierInterfaceId = "office-adapter" };
        var client = new WorkflowOfficeStorageClient(remote, StorageToken, fixture.DirectoryPath, http);
        try
        {
            var switching = model.SwitchStorageAsync(() => Task.FromResult(new WorkflowConnection(client.CreateStore(),
                Office: remote, RemoteClient: client)), cancel.Token, () => throw new InvalidOperationException("Unexpected commit"));
            await read.Task.WaitAsync(TestContext.Current.CancellationToken);
            cancel.Cancel();
            Assert.NotNull(await switching);
            Assert.Equal(saved, preferences.Load());
            Assert.Equal(local.Id, model.DatabaseId);
            Assert.False(model.IsRemoteStorage);
            Assert.False(File.Exists(remote.CredentialPath(fixture.DirectoryPath)));
        }
        finally { model.Detach(); }
    }

    [Fact]
    public async Task ReportManifestKeepsReadableReportsWhenNeighborIsLocked()
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var project = await fixture.AddAsync();
        var token = TestContext.Current.CancellationToken;
        var folder = Path.GetDirectoryName(project.NativePath!)!;
        var readable = Path.GetFileName(ProjectFixture.ReportPath(project));
        await ProjectFixture.WriteAsync(project);
        var lockedName = "Соседний.html";
        await using var locked = File.Open(Path.Combine(folder, lockedName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var result = await WorkflowOfficeProjectPaths.ReportManifestAsync(folder, project.ImportProfile!, null, token);
        Assert.Equal(readable, Assert.Single(result.Files).RelativePath);
        Assert.Equal(lockedName, Assert.Single(result.UnavailableFiles));
        var selected = await WorkflowOfficeProjectPaths.ReportManifestAsync(folder, project.ImportProfile!, readable, token);
        Assert.Equal(readable, Assert.Single(selected.Files).RelativePath);
        Assert.Empty(selected.UnavailableFiles);
    }

    [Fact]
    public async Task CalculationDownloadsOnlyReportsAndLaterFolderOpenKeepsLocalEdits()
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var project = await fixture.AddAsync();
        var token = TestContext.Current.CancellationToken;
        var order = (await fixture.Store.ReadAsync(token)).Orders.Single();
        var report = "Спецификация.html";
        var drawing = "Чертёж.dxf";
        var bytes = "Содержимое"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var downloads = 0;
        using var http = new HttpClient(new Handler((message, _) =>
        {
            if (message.RequestUri!.AbsolutePath.EndsWith("/file", StringComparison.Ordinal))
            {
                downloads++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            }
            var reportsOnly = message.RequestUri.Query.StartsWith("?reportProfile=", StringComparison.Ordinal);
            var names = reportsOnly ? new[] { report } : new[] { report, drawing };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
            {
                directories = Array.Empty<string>(), files = names.Select(path => new { path, length = bytes.Length, sha256 = hash })
            }) });
        }));
        var client = new WorkflowOfficeStorageClient(new(Address, Guid.NewGuid(), Guid.NewGuid(), true), StorageToken, fixture.DirectoryPath, http, http);
        var (folder, _, _) = await client.OpenCalculationFolderAsync(order.Id, project, null, token);
        Assert.Equal(1, downloads);
        Assert.False(File.Exists(Path.Combine(folder, drawing)));
        await File.WriteAllTextAsync(Path.Combine(folder, report), "Моя правка", token);
        Assert.Equal(folder, await client.OpenCopyAsync(order.Id, project.Id, token, nativePath: project.NativePath));
        Assert.Equal(2, downloads);
        Assert.Equal("Моя правка", await File.ReadAllTextAsync(Path.Combine(folder, report), token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(folder, drawing), token));
    }

    [Fact]
    public async Task ServerTimeoutIsDifferentFromCallerCancellation()
    {
        using var fixture = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        using var http = new HttpClient(new Handler((_, _) => throw new TaskCanceledException("Сервер не ответил.", new TimeoutException())));
        var client = new WorkflowOfficeStorageClient(new(Address, Guid.NewGuid(), Guid.NewGuid(), true), StorageToken, fixture.DirectoryPath, http, http);
        var timeout = await Assert.ThrowsAsync<WorkflowValidationException>(() => client.RequireCalculationSupportAsync(token));
        Assert.Contains("Сервер не ответил", timeout.Message);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RequireCalculationSupportAsync(new CancellationToken(true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryingRemoteProjectPreparationReusesTheSameWorkingFile(bool bending)
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var book = await fixture.Store.ReadAsync(token);
        var order = Assert.Single(book.Orders);
        var machine = book.Machines.Single(m => m.Name == "Лазер");
        var id = Guid.NewGuid();
        var time = order.CreatedAt.AddDays(1);
        var first = await WorkflowProjectFiles.PrepareRemoteAsync(book.ProjectRoot!, order, "Заказчик", machine, 6, null, id, time, token, bending);
        var folder = Path.Combine(Path.GetDirectoryName(first.NativePath!)!, WorkflowBendingFiles.FolderName);
        Assert.Equal(bending, first.HasPlannedBending);
        Assert.Equal(bending, Directory.Exists(folder));
        await File.WriteAllTextAsync(first.NativePath!, "Правка после подготовки", token);
        var retried = await WorkflowProjectFiles.PrepareRemoteAsync(book.ProjectRoot!, order, "Заказчик", machine, 6, null, id, time.AddSeconds(1), token, bending);
        Assert.Equal(first, retried);
        Assert.Equal("Правка после подготовки", await File.ReadAllTextAsync(first.NativePath!, token));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowProjectFiles.PrepareRemoteAsync(book.ProjectRoot!, order,
            "Заказчик", machine, 8, null, id, time, token));
        if (bending)
        {
            var withoutBending = await WorkflowProjectFiles.PrepareRemoteAsync(book.ProjectRoot!, order, "Заказчик", machine, 6, null, id, time, token);
            Assert.Equal(first with { HasPlannedBending = false }, withoutBending);
            Assert.True(Directory.Exists(folder));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderOfficeServerStillCreatesOrdinaryProjectsButCannotLoseTheBendingChoice(bool supported)
    {
        using var fixture = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        var projectId = Guid.NewGuid(); var orderId = Guid.NewGuid(); var actor = Guid.NewGuid(); var machine = Guid.NewGuid();
        var request = new WorkflowOfficeProjectPreparation(projectId, 1, actor, machine, 6, null, true);
        var posts = 0;
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            if (message.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { guardedCalculation = true, plannedBending = supported }) };
            ++posts;
            var input = (await message.Content!.ReadFromJsonAsync<WorkflowOfficeProjectPreparation>(cancellation))!;
            Assert.Equal(request with { HasPlannedBending = supported }, input);
            var project = new CuttingProject(projectId, Guid.NewGuid(), 1, machine, 6, "Проект",
                @"C:\Projects\project.rcam", "block-html/v1", DateTimeOffset.Now) { HasPlannedBending = input.HasPlannedBending };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(project) };
        }));
        var client = new WorkflowOfficeStorageClient(new(Address, Guid.NewGuid(), Guid.NewGuid(), true), StorageToken, fixture.DirectoryPath, http, http);
        if (!supported)
        {
            var error = await Assert.ThrowsAsync<WorkflowValidationException>(() => client.PrepareProjectAsync(orderId, request, token));
            Assert.Contains("обновите", error.Message);
            Assert.Equal(0, posts);
            request = request with { HasPlannedBending = false };
        }
        var created = await client.PrepareProjectAsync(orderId, request, token);
        Assert.Equal(supported, created.HasPlannedBending);
        Assert.Equal(1, posts);
    }

    [Fact]
    public async Task LegacyOfficeWritePreservesPlannedBendingAndRejectsTheFirstCalculation()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var token = TestContext.Current.CancellationToken;
        var order = (await f.Store.ReadAsync(token)).Orders.Single();
        var marked = order with { Projects = [project with { HasPlannedBending = true }] };
        var oldEdit = order with { Version = order.Version + 1, Blcb = "010001" };
        var patch = new WorkflowAccessPatch(null, 0, null, [new(oldEdit.Id, order.Version, oldEdit)]);
        var preserved = patch.PreservePlannedBending(marked, oldEdit);
        Assert.Equal("010001", preserved.Blcb);
        Assert.True(Assert.Single(preserved.Projects).HasPlannedBending);

        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var calculated = (await f.Store.ReadAsync(token)).Orders.Single();
        var error = Assert.Throws<WorkflowValidationException>(() => patch.PreservePlannedBending(marked, calculated));
        Assert.Contains("обновите программу", error.Message);
        var updatedClient = patch with { SupportsPlannedBending = true };
        Assert.Same(oldEdit, updatedClient.PreservePlannedBending(marked, oldEdit));
        Assert.Same(calculated, updatedClient.PreservePlannedBending(marked, calculated));
    }

    [Theory]
    [InlineData(@"C:\Проекты\Заявка\.workflow-reports\Спецификация.html")]
    [InlineData(@"\\fileserver\Проекты\Заявка\.workflow-reports\Спецификация.html")]
    [InlineData(@"\\?\UNC\fileserver\Проекты\Заявка\.workflow-reports\Спецификация.html")]
    [InlineData(@"\\?\C:\Проекты\Заявка\.workflow-reports\Спецификация.html")]
    public async Task MainJournalSwitchesItsSourceWithoutLosingLocalMdbSettings(string archivePath)
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var project = await fixture.AddAsync();
        await ProjectFixture.WriteAsync(project);
        await fixture.AcceptAsync();
        var prepared = await fixture.Store.ReadAsync(TestContext.Current.CancellationToken);
        var order = Assert.Single(prepared.Orders);
        var specification = Assert.Single(order.Specifications);
        var report = Assert.IsType<AcceptedReport>(specification.Current.Report) with { ArchivePath = archivePath };
        order = order with { Specifications = [specification with { Current = specification.Current with { Report = report } }] };
        var local = new MemoryMdb();
        var remote = new MemoryMdb { Book = prepared with { Orders = [order] } };
        var employee = remote.Book.Employees.First(person => person.CanWork);
        var preferences = new WorkflowPreferencesStore(Path.Combine(fixture.DirectoryPath, "office-preferences.json"));
        var localPath = Path.Combine(fixture.DirectoryPath, "local.mdb");
        preferences.Save(new() { DatabasePath = localPath, DatabaseId = local.Id,
            DatabaseEmployees = new() { [remote.Id.ToString("N")] = employee.Id } });
        var model = new WorkflowViewModel(new WorkflowMdbStore(localPath, local, local.Id), new(new WorkflowAppUpdater()),
            preferences: preferences, databases: local);
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            Assert.Equal("/api/v1/storage/database", message.RequestUri!.AbsolutePath);
            Assert.Equal(StorageToken, message.Headers.Authorization?.Parameter);
            var request = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            Assert.Empty(request.Path);
            Assert.Equal(remote.Id, request.DatabaseId);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(await remote.SendAsync(request, cancellation)) };
        }));
        var settings = new WorkflowOfficeStorageSettings(Address, remote.Id, Guid.NewGuid(), true) { ZeroTierInterfaceId = "test-interface" };
        var office = new WorkflowOfficeStorageClient(settings, StorageToken, fixture.DirectoryPath, http);
        var token = TestContext.Current.CancellationToken;
        try
        {
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.Null(await model.SwitchStorageAsync(() => Task.FromResult(new WorkflowConnection(office.CreateStore(),
                Office: settings, RemoteClient: office)), token));
            Assert.True(model.IsRemoteStorage);
            Assert.Equal(remote.Id, model.DatabaseId);
            Assert.Equal(employee.Id, model.CurrentEmployee?.Id);
            Assert.Single(model.Orders);
            if (!OperatingSystem.IsWindows())
            {
                var error = await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowReportFiles.VerifyArchiveAsync(report, token));
                Assert.Contains("на этом компьютере", error.Message);
            }
            Assert.Equal(localPath, preferences.Load().DatabasePath);
            Assert.Equal(settings, preferences.Load().OfficeStorage);
            Assert.True(preferences.Load().UseOfficeStorage);
            Assert.Equal(StorageToken, WorkflowOfficeConnection.Load(settings.CredentialPath(fixture.DirectoryPath))!.Token);
            using var storageSettings = model.CreateGeneralSettings();
            storageSettings.SelectedSectionIndex = 1;
            Assert.False(storageSettings.CanUseSavedOfficeStorage);
            var copies = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "Локальные копии")).FullName;
            storageSettings.OfficeCopiesRoot = copies;
            await storageSettings.SaveCommand.ExecuteAsync(null);
            Assert.Null(storageSettings.Error);
            Assert.Equal(copies, preferences.Load().OfficeCopiesRoot);
            model.NewOrderCommand.Execute(null);
            model.NewManager = "Менеджер из дома";
            model.NewCustomer = "Заказчик из дома";
            await model.CreateOrderCommand.ExecuteAsync(null);
            Assert.Null(model.Error);
            Assert.Empty(local.Book.Orders);
            Assert.Contains(remote.Book.Orders, order => order.CreatedById == employee.Id &&
                remote.Book.Customers.Single(customer => customer.Id == order.CustomerId).Name == "Заказчик из дома");
            Assert.Equal(report, remote.Book.Orders.Single(saved => saved.Id == order.Id).Specifications.Single().Current.Report);
            Assert.Null(await model.ConnectDatabaseAsync(localPath, false, token));
            Assert.False(model.IsRemoteStorage);
            Assert.Equal(local.Id, model.DatabaseId);
            Assert.Equal(settings, preferences.Load().OfficeStorage);
            Assert.False(preferences.Load().UseOfficeStorage);
            Assert.Equal(copies, preferences.Load().OfficeCopiesRoot);
            using var localSettings = model.CreateGeneralSettings();
            Assert.True(localSettings.CanUseSavedOfficeStorage);
            Assert.Equal("Настроить подключение…", localSettings.OfficeConnectionLabel);
        }
        finally { model.Detach(); }
    }

    [Theory]
    [InlineData(@".workflow-reports\Спецификация.html")]
    [InlineData(@"C:Спецификация.html")]
    [InlineData(@"\Проекты\Спецификация.html")]
    public void ArchiveSnapshotStillRejectsRelativePaths(string archivePath)
    {
        var report = new AcceptedReport(Guid.NewGuid(), new('A', 64), 1, "Спецификация.html", archivePath,
            new("", "", "", "", "", "", "", "", "", "", ""));
        Assert.Throws<WorkflowValidationException>(report.Validate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadedProjectKeepsItsPathAndVersionWhenThePreferredFolderChanges(bool customRoot)
    {
        using var fixture = new ProjectFixture();
        const string OrderFolder = "2026-11-16-101500-1a2b3c4d Учебный заказчик";
        const string ProjectFolder = "20 mm 2026-11-18-102615--HANSLASER";
        var native = $@"\\fileserver\Projects\2026\11\16\{OrderFolder}\{ProjectFolder}\{ProjectFolder}.Dsp";
        var contents = "Исходный файл"u8.ToArray();
        var originalHash = Convert.ToHexString(SHA256.HashData(contents));
        var uploads = 0;
        using var http = new HttpClient(new Handler(async (message, cancellation) =>
        {
            var path = message.RequestUri!.AbsolutePath;
            Assert.Equal(StorageToken, message.Headers.Authorization?.Parameter);
            if (path.EndsWith("/files", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                {
                    directories = new[] { "ГИБКА" }, files = new[] { new { path = "Карта.pdf", length = contents.Length, sha256 = originalHash } }
                }) };
            Assert.EndsWith("/file", path, StringComparison.Ordinal);
            if (message.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(contents) };
            Assert.Equal(HttpMethod.Put, message.Method);
            Assert.Equal($"\"{originalHash}\"", Assert.Single(message.Headers.IfMatch).Tag);
            var uploaded = await message.Content!.ReadAsByteArrayAsync(cancellation);
            Assert.Equal("Файл после правки", System.Text.Encoding.UTF8.GetString(uploaded));
            uploads++;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { path = "Карта.pdf", sha256 = Convert.ToHexString(SHA256.HashData(uploaded)) }) };
        }));
        var client = new WorkflowOfficeStorageClient(new(Address, Guid.NewGuid(), Guid.NewGuid(), true), StorageToken, fixture.DirectoryPath, http, http);
        var order = Guid.NewGuid(); var project = Guid.NewGuid();
        var token = TestContext.Current.CancellationToken;
        var copies = customRoot ? Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "Локальные копии")).FullName : null;
        var folder = await client.OpenCopyAsync(order, project, token, copies, native);
        Assert.Equal(Path.Combine(copies ?? Path.Combine(fixture.DirectoryPath, "office-projects"),
            "2026", "11", "16", OrderFolder, ProjectFolder), folder);
        Assert.True(Directory.Exists(Path.Combine(folder, "ГИБКА")));
        var file = Path.Combine(folder, "Карта.pdf");
        Assert.Equal(contents, await File.ReadAllBytesAsync(file, token));
        await File.WriteAllTextAsync(file, "Файл после правки", token);
        var next = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "Новый путь")).FullName;
        var restarted = new WorkflowOfficeStorageClient(client.Settings, StorageToken, fixture.DirectoryPath, http, http);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => restarted.OpenCopyAsync(order, Guid.NewGuid(), token, copies, native));
        Assert.Equal(folder, await restarted.OpenCopyAsync(order, project, token, next, native));
        Assert.Equal(1, (await restarted.PublishCopyAsync(order, project, token, next, native)).Sent);
        Assert.Equal(1, uploads);
        Assert.Equal("Файл после правки", await File.ReadAllTextAsync(file, token));
        if (customRoot)
        {
            Directory.Move(folder, folder + ".backup");
            await Assert.ThrowsAsync<WorkflowValidationException>(() => restarted.OpenCopyAsync(order, project, token, next, native));
            Assert.Empty(Directory.GetFileSystemEntries(next));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningExistingCopyKeepsLegacyLocationAndEdits(bool customRoot)
    {
        using var fixture = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        var settings = new WorkflowOfficeStorageSettings(Address, Guid.NewGuid(), Guid.NewGuid(), true);
        var order = Guid.NewGuid();
        var project = Guid.NewGuid();
        var profileFolder = Path.Combine(fixture.DirectoryPath, "office-projects", settings.DatabaseId.ToString("N"));
        var folder = Path.Combine(customRoot ? Path.Combine(fixture.DirectoryPath, "Копии", settings.DatabaseId.ToString("N")) : profileFolder,
            project.ToString("N"));
        Directory.CreateDirectory(folder);
        var original = Path.Combine(folder, "Моя правка.txt");
        await File.WriteAllTextAsync(original, "Правка технолога", token);
        await File.WriteAllTextAsync(Path.Combine(folder, ".office-copy.json"), "{}", token);
        if (customRoot)
        {
            Directory.CreateDirectory(profileFolder);
            await File.WriteAllTextAsync(Path.Combine(profileFolder, project.ToString("N") + ".location"), folder, token);
        }

        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.EndsWith("/files", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new
            { directories = Array.Empty<string>(), files = Array.Empty<object>() }) });
        }));
        var client = new WorkflowOfficeStorageClient(settings, StorageToken, fixture.DirectoryPath, http, http);
        var path = await client.OpenCopyAsync(order, project, token,
            nativePath: @"D:\Projects\2026\11\16\Заявка\Проект\Проект.dsp");
        Assert.Equal(folder, path);
        Assert.Equal("Правка технолога", await File.ReadAllTextAsync(original, token));
        if (OperatingSystem.IsWindows())
            Assert.True(File.GetAttributes(Path.Combine(folder, ".office-copy.json")).HasFlag(FileAttributes.Hidden));
    }

    [Fact]
    public async Task ReopeningRefreshesOfficeFilesPreservesEditsAndTracksPendingFilesUntilPublish()
    {
        using var fixture = new WorkingFiles();
        var token = TestContext.Current.CancellationToken;
        fixture.Office.Add("Проект.rcam", "Первая офисная версия");
        fixture.Office.Add("Удаляемый.nc", "Копия до удаления");
        fixture.Office.Add("Чертёж.dxf", "Исходный чертёж");
        var first = await fixture.OpenAsync(token);
        Assert.False(first.HasPendingFiles);
        var baseline = Path.Combine(first.Folder, ".office-copy.json");
        var owner = Path.Combine(first.Folder, ".office-copy-owner");
        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.GetAttributes(baseline).HasFlag(FileAttributes.Hidden));
            Assert.True(File.GetAttributes(owner).HasFlag(FileAttributes.Hidden));
            Assert.False(File.GetAttributes(first.Folder).HasFlag(FileAttributes.Hidden));
        }
        await File.WriteAllTextAsync(Path.Combine(first.Folder, "Чертёж.dxf"), "Домашняя правка", token);
        await File.WriteAllTextAsync(Path.Combine(first.Folder, "Домашний.nc"), "Добавлен дома", token);
        fixture.Office["Проект.rcam"] = "Вторая офисная версия";
        fixture.Office.Remove("Удаляемый.nc");
        fixture.Office.Add("Новый.nc", "Добавлен в офисе");

        var opened = await fixture.OpenAsync(token);
        Assert.Equal(first.Folder, opened.Folder);
        Assert.Empty(opened.Conflicts);
        Assert.True(opened.HasPendingFiles);
        Assert.Equal("Вторая офисная версия", await File.ReadAllTextAsync(Path.Combine(opened.Folder, "Проект.rcam"), token));
        Assert.Equal("Добавлен в офисе", await File.ReadAllTextAsync(Path.Combine(opened.Folder, "Новый.nc"), token));
        Assert.Equal("Домашняя правка", await File.ReadAllTextAsync(Path.Combine(opened.Folder, "Чертёж.dxf"), token));
        Assert.False(File.Exists(Path.Combine(opened.Folder, "Удаляемый.nc")));
        var recovered = Assert.Single(Directory.EnumerateFiles(Path.Combine(first.Folder, ".office-sync"), "Удаляемый.nc", SearchOption.AllDirectories));
        Assert.Equal("Копия до удаления", await File.ReadAllTextAsync(recovered, token));
        Assert.Empty(fixture.Uploads);
        Assert.True(await fixture.PendingAsync(token));
        Assert.Equal(2, (await fixture.Client.PublishCopyAsync(fixture.Order, fixture.Project, token)).Sent);
        Assert.Equal("Домашняя правка", fixture.Office["Чертёж.dxf"]);
        Assert.Equal("Добавлен дома", fixture.Office["Домашний.nc"]);
        Assert.False(await fixture.PendingAsync(token));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(File.GetAttributes(baseline).HasFlag(FileAttributes.Hidden));
            Assert.True(File.GetAttributes(Path.Combine(first.Folder, ".office-sync")).HasFlag(FileAttributes.Hidden));
            Assert.False(File.GetAttributes(Path.Combine(first.Folder, "Проект.rcam")).HasFlag(FileAttributes.Hidden));
            Assert.False(File.GetAttributes(Path.Combine(first.Folder, "Новый.nc")).HasFlag(FileAttributes.Hidden));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ReopeningConflictingFileRequiresAChoiceAndKeepsBothVersions(bool deletedInOffice, bool useOffice)
    {
        using var fixture = new WorkingFiles();
        var token = TestContext.Current.CancellationToken;
        const string name = "Проект.rcam";
        fixture.Office.Add(name, "Исходный файл");
        var first = await fixture.OpenAsync(token);
        var local = Path.Combine(first.Folder, name);
        await File.WriteAllTextAsync(local, "Моя версия", token);
        if (deletedInOffice) fixture.Office.Remove(name);
        else fixture.Office[name] = "Офисная версия";

        var copy = await fixture.OpenAsync(token);
        var conflict = Assert.Single(copy.Conflicts);
        Assert.Equal(name, conflict.Path);
        Assert.True(copy.HasPendingFiles);
        Assert.Equal("Моя версия", await File.ReadAllTextAsync(local, token));
        var comparison = await fixture.Client.CompareWorkingFileAsync(fixture.Order, fixture.Project, conflict, token);
        Assert.Equal("Моя версия", await File.ReadAllTextAsync(Path.Combine(comparison.Folder, "Локальная.rcam"), token));
        if (deletedInOffice) Assert.Null(comparison.OfficeFile);
        else Assert.Equal("Офисная версия", await File.ReadAllTextAsync(comparison.OfficeFile!, token));

        fixture.Office[name] = "После сравнения";
        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            fixture.Client.ChooseWorkingFileAsync(fixture.Order, fixture.Project, conflict, comparison, useOffice, token));
        Assert.Equal("Моя версия", await File.ReadAllTextAsync(local, token));
        if (deletedInOffice) fixture.Office.Remove(name);
        else fixture.Office[name] = "Офисная версия";
        await fixture.Client.ChooseWorkingFileAsync(fixture.Order, fixture.Project, conflict, comparison, useOffice, token);
        Assert.Empty(fixture.Uploads);
        copy = await fixture.OpenAsync(token);
        Assert.Empty(copy.Conflicts);
        Assert.Equal(!useOffice, copy.HasPendingFiles);
        if (useOffice && deletedInOffice) Assert.False(File.Exists(local));
        else Assert.Equal(useOffice ? "Офисная версия" : "Моя версия", await File.ReadAllTextAsync(local, token));
        if (!useOffice)
        {
            Assert.Equal(1, (await fixture.Client.PublishCopyAsync(fixture.Order, fixture.Project, token)).Sent);
            Assert.Equal("Моя версия", fixture.Office[name]);
        }
        Assert.False(await fixture.PendingAsync(token));
    }

    private sealed class WorkingFiles : IDisposable
    {
        private readonly ProjectFixture _temp = new();
        private readonly HttpClient _http;
        public Dictionary<string, string> Office { get; } = new(StringComparer.Ordinal);
        public List<string> Uploads { get; } = [];
        public string DirectoryPath => _temp.DirectoryPath;
        public Guid Order { get; } = Guid.NewGuid();
        public Guid Project { get; } = Guid.NewGuid();
        public WorkflowOfficeStorageClient Client { get; }
        public Func<WorkflowAccessRequest, WorkflowAccessResponse>? ReadDatabase { get; set; }
        public HttpStatusCode? DatabaseStatus { get; set; }
        public Func<CancellationToken, Task>? BeforeUpload { get; set; }
        public bool ManifestUnavailable { get; set; }
        public HttpStatusCode? ManifestStatus { get; set; }
        public bool Offline { get; set; }

        public WorkingFiles()
        {
            _http = new HttpClient(new Handler(RespondAsync));
            Client = new(new(Address, Guid.NewGuid(), Guid.NewGuid(), true), StorageToken, _temp.DirectoryPath, _http, _http);
        }

        public Task<WorkflowOfficeWorkingCopy> OpenAsync(CancellationToken token) => Client.OpenWorkingCopyAsync(Order, Project, token);
        public Task<bool?> PendingAsync(CancellationToken token) => Client.HasPendingFilesAsync(Project, null, null, token);
        private static byte[] Bytes(string contents) => System.Text.Encoding.UTF8.GetBytes(contents);
        private static string Hash(string contents) => Convert.ToHexString(SHA256.HashData(Bytes(contents)));

        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken token)
        {
            var route = request.RequestUri!;
            if (route.AbsolutePath == "/api/v1/storage/database" && DatabaseStatus is { } databaseStatus)
                return new(databaseStatus);
            if (route.AbsolutePath == "/api/v1/storage/database" && ReadDatabase is { } database)
            {
                var operation = (await request.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(token))!;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(database(operation)) };
            }
            if (route.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            {
                if (Offline) throw new HttpRequestException("Нет связи с офисом.");
                if (ManifestStatus is { } status) return new(status);
                if (ManifestUnavailable) return new(HttpStatusCode.Conflict);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                { directories = Array.Empty<string>(), files = Office.Select(pair => new { path = pair.Key, length = Bytes(pair.Value).Length, sha256 = Hash(pair.Value) }) }) };
            }
            Assert.EndsWith("/file", route.AbsolutePath, StringComparison.Ordinal);
            var name = Uri.UnescapeDataString(route.Query["?path=".Length..]);
            if (request.Method == HttpMethod.Get) return Office.TryGetValue(name, out var content)
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(content)) } : new(HttpStatusCode.NotFound);
            Assert.Equal(HttpMethod.Put, request.Method);
            if (BeforeUpload is { } before) await before(token);
            var matches = Office.TryGetValue(name, out var current)
                ? request.Headers.IfMatch.SingleOrDefault()?.Tag == $"\"{Hash(current)}\""
                : request.Headers.IfNoneMatch.SingleOrDefault()?.Tag == "*";
            if (!matches) return new(HttpStatusCode.PreconditionFailed);
            Office[name] = await request.Content!.ReadAsStringAsync(token);
            Uploads.Add(name);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { path = name, sha256 = Hash(Office[name]) }) };
        }

        public void Dispose()
        {
            _http.Dispose();
            _temp.Dispose();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => reply(request, cancellationToken);
    }
}
