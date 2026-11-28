using System.Net;
using System.Net.Http.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed partial class WorkflowNativeMdbTests
{
    private static string NativeExecutable()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Journal.Next.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        var executable = Environment.GetEnvironmentVariable("WORKFLOW_TEST_ACCESS_HOST") ?? Path.Combine(root.FullName,
            "src", "Jornal.Workflow.Desktop", "bin", "Release", "net10.0", "AccessHost", "Jornal.AccessHost.exe");
        Assert.True(File.Exists(executable), "Build the Windows Desktop project to prepare its self-contained x86 AccessHost.");
        return executable;
    }

    [Fact]
    public async Task CalculationFilePublicationUsesCurrentJetOrderVersionWithoutChangingTheDatabase()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("The guarded file publication requires Windows and the x86 AccessHost."); return; }
        using var fixture = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.DirectoryPath, "Publication.mdb");
        var root = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "Projects")).FullName;
        var archives = Directory.CreateDirectory(Path.Combine(root, "Project", ".workflow-reports")).FullName;
        var access = new WorkflowAccessClient(NativeExecutable());
        var database = (await access.SendAsync(new(1, WorkflowAccessOperation.Create, path), token)).DatabaseId;
        var store = new WorkflowMdbStore(path, access, database);
        var book = await store.SaveEmployeeProfileAsync(null, "Технолог проверки", 0xFF628DB2, token);
        book = await store.WriteAsync(book.Version, book => book with { ProjectRoot = root }, token);
        var actor = Assert.Single(book.Employees).Id;
        var (opened, id) = await store.CreateOrderFromNamesAsync("Менеджер проверки", "Заказчик проверки", actor, DateTimeOffset.UtcNow, token);
        var order = Assert.Single(opened.Orders);
        var latest = await store.WriteOrderAsync(id, order.Version, current => WorkflowChanges.UpdateOrder(current,
            id, order.Version, order.ManagerId, order.TechnologistId, "БЛЦБ-123456", actor, DateTimeOffset.UtcNow), token);
        var file = Path.Combine(Path.GetDirectoryName(archives)!, "specification.html");
        var stagedId = Guid.NewGuid();
        var staged = Path.Combine(archives, $"{stagedId:N}.tmp");
        const string previous = "Рабочая спецификация в офисе";
        await File.WriteAllTextAsync(file, previous, token);
        await File.WriteAllTextAsync(staged, "Спецификация из пакета", token);
        var (_, hash) = await WorkflowOfficeProjectPaths.FingerprintAsync(file, token);
        var (_, stagedHash) = await WorkflowOfficeProjectPaths.FingerprintAsync(staged, token);
        var request = new WorkflowCalculationFilePublication(id, order.Version, root, file, stagedId, stagedHash, hash);

        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.PublishCalculationFileAsync(request, token));
        Assert.Equal(previous, await File.ReadAllTextAsync(file, token));
        Assert.True(File.Exists(staged));

        var currentOrder = Assert.Single(latest.Orders);
        await store.PublishCalculationFileAsync(request with { OrderVersion = currentOrder.Version }, token);
        Assert.Equal("Спецификация из пакета", await File.ReadAllTextAsync(file, token));
        Assert.Empty(Directory.EnumerateFiles(archives));
        var readback = await store.ReadOrderAsync(id, token);
        Assert.Equal(latest.Version, readback.Version);
        Assert.Equal(currentOrder.Version, Assert.Single(readback.Orders).Version);
    }

    [Fact]
    public async Task RemoteCreationRecoversThroughRealJetAfterResponsesAreLost()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Remote creation recovery through the native MDB host requires Windows."); return; }
        using var fixture = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(fixture.DirectoryPath, "Recovery.mdb");
        var projectRoot = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "projects")).FullName;
        var profile = Directory.CreateDirectory(Path.Combine(fixture.DirectoryPath, "client")).FullName;
        var access = new WorkflowAccessClient(NativeExecutable());
        var database = (await access.SendAsync(new(1, WorkflowAccessOperation.Create, path), token)).DatabaseId;
        var office = new WorkflowMdbStore(path, access, database);
        var book = await office.SaveEmployeeProfileAsync(null, "Технолог проверки", 0xFF628DB2, token);
        var actor = Assert.Single(book.Employees).Id;
        var preparations = new List<CuttingProject>();
        Guid? orderId = null;
        var orderWrites = 0;
        var projectWrites = 0;
        using var http = new HttpClient(new RecoveryHandler(async (message, cancellation) =>
        {
            var route = message.RequestUri!.AbsolutePath;
            if (route.EndsWith("/capabilities", StringComparison.Ordinal))
                return RecoveryReply(new { guardedCalculation = true, plannedBending = true });
            if (route.EndsWith("/prepare", StringComparison.Ordinal))
            {
                var request = (await message.Content!.ReadFromJsonAsync<WorkflowOfficeProjectPreparation>(cancellation))!;
                var snapshot = await access.SendAsync(new(1, WorkflowAccessOperation.ReadOrders, path, database,
                    OrderIds: [orderId!.Value], OfficeSchema6: true), cancellation);
                var order = Assert.Single(snapshot.Orders!);
                Assert.Equal(order.Version, request.OrderVersion);
                var machine = snapshot.Catalog!.Machines.Single(m => m.Id == request.MachineId);
                var customer = snapshot.Catalog.Customers.Single(c => c.Id == order.CustomerId);
                var prepared = await WorkflowProjectFiles.PrepareRemoteAsync(projectRoot, order, customer.Name, machine,
                    request.ThicknessMm, request.ItemId, request.ProjectId, DateTimeOffset.Now, cancellation, request.HasPlannedBending);
                preparations.Add(prepared);
                if (preparations.Count == 1) throw new HttpRequestException("Lost response after preparing the file.");
                return RecoveryReply(prepared);
            }
            Assert.EndsWith("/database", route);
            var command = (await message.Content!.ReadFromJsonAsync<WorkflowAccessRequest>(cancellation))!;
            WorkflowOrder? value = null;
            if (command.Operation == WorkflowAccessOperation.Apply)
            {
                value = Assert.Single(command.Patch!.Orders).Value;
                if (value!.Projects.Length == 0)
                {
                    orderId ??= value.Id;
                    Assert.Equal(orderId, value.Id);
                    if (++orderWrites == 1) throw new HttpRequestException("Lost connection before inserting the order.");
                }
            }
            var response = await access.SendAsync(command with
            {
                Operation = command.Operation == WorkflowAccessOperation.Inspect ? WorkflowAccessOperation.ReadOrders : command.Operation,
                Path = path, OfficeSchema6 = true
            }, cancellation);
            if (value?.Projects.Length > 0 && ++projectWrites == 1)
                throw new HttpRequestException("Lost response after committing the project.");
            return RecoveryReply(response);
        }));

        var settings = new WorkflowOfficeStorageSettings("http://10.8.0.2:5197/", database, Guid.NewGuid(), true);
        var preferences = new WorkflowPreferencesStore(Path.Combine(profile, "preferences.json"));
        var saved = new WorkflowPreferences { EmployeeId = actor, UseOfficeStorage = true, OfficeStorage = settings };
        preferences.Save(saved);
        WorkflowViewModel OpenClient()
        {
            var client = new WorkflowOfficeStorageClient(settings, "", profile, http);
            return new(client.CreateStore(), new(new WorkflowAppUpdater()), preferences: preferences, initialPreferences: saved, remoteTransport: client);
        }

        var first = OpenClient();
        try
        {
            await first.ReloadCommand.ExecuteAsync(null);
            Assert.Null(first.Error);
            first.NewOrderCommand.Execute(null);
            first.NewManager = "Менеджер проверки";
            first.NewCustomer = "Заказчик проверки";
            await first.CreateOrderCommand.ExecuteAsync(null);
            Assert.Equal("Ожидает подтверждения", Assert.Single(first.QueuedOfficeOrderRows).Status);
            Assert.Empty((await office.ReadAsync(token)).Orders);
        }
        finally { first.Detach(); }

        var resumed = OpenClient();
        try
        {
            await resumed.ReloadCommand.ExecuteAsync(null);
            Assert.Single(resumed.QueuedOfficeOrderRows);
            await resumed.SynchronizeOfficeOrdersAsync(DateTimeOffset.UtcNow, token);
            Assert.Empty(resumed.QueuedOfficeOrderRows);
            Assert.Null(resumed.Error);
            resumed.SelectedOrder = Assert.Single(resumed.Orders);
            resumed.OpenOrderCommand.Execute(null);
            resumed.AddProjectCommand.Execute(null);
            resumed.PlanMachine = resumed.ProjectMachines.Single(m => m.Name == "Лазер");
            resumed.PlanThickness = "6";
            resumed.PlanHasBending = true;
            await resumed.CreateProjectCommand.ExecuteAsync(null);
            Assert.NotNull(resumed.Error);
            Assert.Empty(Assert.Single((await office.ReadAsync(token)).Orders).Projects);
        }
        finally { resumed.Detach(); }

        var preparedFile = Assert.Single(preparations);
        await File.WriteAllTextAsync(preparedFile.NativePath!, "Офисная правка в заготовке", token);
        var retry = OpenClient();
        try
        {
            await retry.ReloadCommand.ExecuteAsync(null);
            retry.SelectedOrder = Assert.Single(retry.Orders);
            retry.OpenOrderCommand.Execute(null);
            retry.AddProjectCommand.Execute(null);
            Assert.True(retry.PlanHasBending);
            await retry.CreateProjectCommand.ExecuteAsync(null);
            Assert.Contains("не подтвердил сохранение проекта", retry.Error);
        }
        finally { retry.Detach(); }

        var committed = Assert.Single((await office.ReadAsync(token)).Orders);
        await office.WriteOrderAsync(committed.Id, committed.Version, current => WorkflowChanges.UpdateOrder(
            current, committed.Id, committed.Version, committed.ManagerId, committed.TechnologistId, "010021", actor, DateTimeOffset.UtcNow), token);
        var confirmed = OpenClient();
        try
        {
            await confirmed.ReloadCommand.ExecuteAsync(null);
            confirmed.SelectedOrder = Assert.Single(confirmed.Orders);
            confirmed.OpenOrderCommand.Execute(null);
            confirmed.AddProjectCommand.Execute(null);
            await confirmed.CreateProjectCommand.ExecuteAsync(null);
            Assert.Null(confirmed.Error);
            Assert.Equal(preparedFile.Id, confirmed.SelectedProject?.Project.Id);
            Assert.Equal("010021", confirmed.Blcb);
        }
        finally { confirmed.Detach(); }
        Assert.Equal(2, orderWrites);
        Assert.Equal(1, projectWrites);
        Assert.Equal(2, preparations.Count);
        Assert.All(preparations, p => Assert.Equal(preparedFile.NativePath, p.NativePath));
        Assert.Equal("Офисная правка в заготовке", await File.ReadAllTextAsync(preparedFile.NativePath!, token));
        Assert.Single(Directory.EnumerateFiles(projectRoot, "*.rcam", SearchOption.AllDirectories));
    }

    private static HttpResponseMessage RecoveryReply<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class RecoveryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
