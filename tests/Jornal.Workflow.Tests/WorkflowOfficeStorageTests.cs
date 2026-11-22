using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowOfficeStorageTests
{
    private const string Address = "http://10.0.0.2:60913/";
    private const string StorageToken = "storage.example";

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

    [Fact]
    public async Task RetryingRemoteProjectPreparationReusesTheSameWorkingFile()
    {
        using var fixture = new ProjectFixture();
        await fixture.InitializeAsync();
        var token = TestContext.Current.CancellationToken;
        var book = await fixture.Store.ReadAsync(token);
        var order = Assert.Single(book.Orders);
        var machine = book.Machines.Single(m => m.Name == "Лазер");
        var id = Guid.NewGuid();
        var time = order.CreatedAt.AddDays(1);
        var first = await WorkflowProjectFiles.PrepareRemoteAsync(book.ProjectRoot!, order, "Заказчик", machine, 6, null, id, time, token);
        await File.WriteAllTextAsync(first.NativePath!, "Правка после подготовки", token);
        var retried = await WorkflowProjectFiles.PrepareRemoteAsync(book.ProjectRoot!, order, "Заказчик", machine, 6, null, id, time.AddSeconds(1), token);
        Assert.Equal(first, retried);
        Assert.Equal("Правка после подготовки", await File.ReadAllTextAsync(first.NativePath!, token));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowProjectFiles.PrepareRemoteAsync(book.ProjectRoot!, order,
            "Заказчик", machine, 8, null, id, time, token));
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
        var settings = new WorkflowOfficeStorageSettings(Address, remote.Id, Guid.NewGuid(), true);
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
            Assert.Equal("Настроить сервер…", localSettings.OfficeConnectionLabel);
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
            Assert.Equal("Файл после правки", await message.Content!.ReadAsStringAsync(cancellation));
            uploads++;
            return new(HttpStatusCode.OK);
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
        Assert.Equal(1, await restarted.PublishCopyAsync(order, project, token, next, native));
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
        Assert.Equal(2, await fixture.Client.PublishCopyAsync(fixture.Order, fixture.Project, token));
        Assert.Equal("Домашняя правка", fixture.Office["Чертёж.dxf"]);
        Assert.Equal("Добавлен дома", fixture.Office["Домашний.nc"]);
        Assert.False(await fixture.PendingAsync(token));
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
            Assert.Equal(1, await fixture.Client.PublishCopyAsync(fixture.Order, fixture.Project, token));
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
        public Guid Order { get; } = Guid.NewGuid();
        public Guid Project { get; } = Guid.NewGuid();
        public WorkflowOfficeStorageClient Client { get; }

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
            if (route.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new
                { directories = Array.Empty<string>(), files = Office.Select(pair => new { path = pair.Key, length = Bytes(pair.Value).Length, sha256 = Hash(pair.Value) }) }) };
            Assert.EndsWith("/file", route.AbsolutePath, StringComparison.Ordinal);
            var name = Uri.UnescapeDataString(route.Query["?path=".Length..]);
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes(Office[name])) };
            Assert.Equal(HttpMethod.Put, request.Method);
            if (Office.TryGetValue(name, out var current))
                Assert.Equal($"\"{Hash(current)}\"", Assert.Single(request.Headers.IfMatch).Tag);
            else
                Assert.Equal("*", Assert.Single(request.Headers.IfNoneMatch).Tag);
            Office[name] = await request.Content!.ReadAsStringAsync(token);
            Uploads.Add(name);
            return new(HttpStatusCode.OK);
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
