using System.Reflection;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowReviewRegressionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static WorkflowOrder Add(ref WorkflowWorkspace book, string customer, DateTimeOffset at)
    {
        book = WorkflowChanges.CreateOrderFromNames(book, "Менеджер", customer, book.Employees[0].Id, at, out var id);
        return book.Orders.Single(o => o.Id == id);
    }
    private static WorkflowWorkspace Project(WorkflowWorkspace book, Guid id, DateTimeOffset at)
    {
        var order = book.Orders.Single(o => o.Id == id); var machine = book.Machines[0];
        var name = WorkflowProjectNames.Name(machine, 6, at);
        var project = new CuttingProject(Guid.NewGuid(), Guid.NewGuid(), 1, machine.Id, 6, name,
            Path.Combine(Path.GetTempPath(), name, name + WorkflowProjectNames.Extension(machine)), machine.ImportProfile, at);
        return WorkflowProjects.Add(book, id, order.Version, project, book.Employees[0].Id, at);
    }
    [Theory]
    [InlineData(3)] [InlineData(-5)]
    public void RenameKeepsBusinessTimestampAfterUtcRoundtrip(int offset)
    {
        var book = WorkflowTestData.CreateWorkspace(); var at = new DateTimeOffset(2026, 10, 25, 1, 40, 10, TimeSpan.FromHours(offset));
        var order = Add(ref book, "Клиент", at); book = Project(book, order.Id, at);
        var read = WorkflowMdbMapping.Orders(WorkflowMdbMapping.Flatten(book)).Single(); var project = read.Projects.Single();
        var plan = WorkflowProjects.ProposeRename(project, book.Machines[0], 8, order.TechnologistId, at);
        Assert.Equal(WorkflowProjectNames.Name(book.Machines[0], 8, at), plan.Name);
        var editor = new WorkflowProjectEditorViewModel(new WorkflowMdbStore(Path.Combine(Path.GetTempPath(), "preview.mdb")),
            book with { Orders = [read] }, order.Id, project.Id, order.TechnologistId, _ => { });
        editor.Thickness = "8"; Assert.Equal(plan.Name, editor.PreviewName);
    }
    [Fact]
    public async Task ProfileFromAnotherDatabaseIsNotAdoptedByF5OrWrittenIntoItsPreferences()
    {
        using var f = new ProjectFixture(); var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() };
        var book = memory.Book; Add(ref book, "Клиент A", DateTimeOffset.Now); memory.Book = book;
        var pathA = Path.Combine(f.DirectoryPath, "a.mdb"); var pathB = Path.Combine(f.DirectoryPath, "b.mdb");
        var actorA = book.Employees[0].Id; var actorB = Guid.NewGuid(); var databaseB = Guid.NewGuid();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        prefs.Save(new() { DatabasePath = pathA, DatabaseId = memory.Id, EmployeeId = actorA });
        var model = new WorkflowViewModel(new WorkflowMdbStore(pathA, memory, memory.Id), new(new WorkflowAppUpdater()), preferences: prefs);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null); model.ResultText = "Моё письмо";
            prefs.Update(p => p with { DatabasePath = pathB, DatabaseId = databaseB, EmployeeId = actorB });
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.Contains("Подключение", model.Error); Assert.Equal(actorA, model.CurrentEmployee?.Id);
            Assert.Equal(pathA, model.WorkspacePath); Assert.Equal("Моё письмо", model.ResultText);
            memory.Requests.Clear(); using var settings = model.CreateGeneralSettings(); settings.ProfileName = "Новая фамилия";
            await settings.SaveCommand.ExecuteAsync(null);
            Assert.NotNull(settings.Error); Assert.DoesNotContain(memory.Requests, r => r.Operation == WorkflowAccessOperation.Apply);
            Assert.DoesNotContain(memory.Book.Employees, e => e.Name == "Новая фамилия"); Assert.Equal(actorB, prefs.Load().EmployeeId);
        }
        finally { model.Detach(); }
    }
    [Fact]
    public async Task ExplicitConnectionToTheAlreadySelectedDatabaseKeepsBothProfilesCorrect()
    {
        using var f = new ProjectFixture(); var a = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() };
        var b = new MemoryMdb { Book = WorkflowWorkspace.Empty with { Employees = [new(Guid.NewGuid(), "Технолог B") { IsTechnologist = true }] } };
        var pathA = Path.Combine(f.DirectoryPath, "a.mdb"); var pathB = Path.Combine(f.DirectoryPath, "b.mdb");
        var actorA = a.Book.Employees[0].Id; var actorB = b.Book.Employees[0].Id;
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        prefs.Save(new() { DatabasePath = pathA, DatabaseId = a.Id, EmployeeId = actorA });
        var model = new WorkflowViewModel(new WorkflowMdbStore(pathA, a, a.Id), new(new WorkflowAppUpdater()), preferences: prefs, databases: b);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null);
            prefs.Update(p => p with { DatabasePath = pathB, DatabaseId = b.Id, EmployeeId = actorB });
            await model.ReloadCommand.ExecuteAsync(null);
            Assert.Null(await model.ConnectDatabaseAsync(pathB, false, Token));
            Assert.Equal(actorB, model.CurrentEmployee?.Id); Assert.Equal(pathB, model.WorkspacePath);
            Assert.Equal(actorA, prefs.Load().DatabaseEmployees![a.Id.ToString("N")]);
        }
        finally { model.Detach(); }
    }
    [Fact]
    public async Task AnUnavailableRememberedEmployeeCanBeReplacedInTheSameDatabase()
    {
        using var f = new ProjectFixture(); var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() };
        var path = Path.Combine(f.DirectoryPath, "employee.mdb"); var unavailable = Guid.NewGuid();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        prefs.Save(new() { DatabasePath = path, DatabaseId = memory.Id, EmployeeId = unavailable });
        var model = new WorkflowViewModel(new WorkflowMdbStore(path, memory, memory.Id), new(new WorkflowAppUpdater()), preferences: prefs);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null); Assert.Null(model.CurrentEmployee);
            using var settings = model.CreateGeneralSettings(); settings.ProfileName = memory.Book.Employees[0].Name;
            await settings.SaveCommand.ExecuteAsync(null);
            Assert.Null(settings.Error); Assert.Equal(memory.Book.Employees[0].Id, model.CurrentEmployee?.Id);
            Assert.Equal(model.CurrentEmployee!.Id, prefs.Load().EmployeeId);
        }
        finally { model.Detach(); }
    }
    [Fact]
    public async Task ChangingSearchDisablesThePreviousCursorBeforeDebounce()
    {
        using var f = new ProjectFixture(); var memory = new MemoryMdb(); var book = WorkflowTestData.CreateWorkspace();
        var at = new DateTimeOffset(DateTime.Today.AddHours(12));
        for (var i = 0; i < 120; ++i) Add(ref book, "Альфа", at.AddMinutes(i));
        for (var i = 0; i < 120; ++i) Add(ref book, "Бета", at.AddDays(1).AddMinutes(i));
        memory.Book = book; var path = Path.Combine(f.DirectoryPath, "paging.mdb");
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        prefs.Save(new() { DatabasePath = path, DatabaseId = memory.Id, EmployeeId = book.Employees[0].Id });
        var model = new WorkflowViewModel(new WorkflowMdbStore(path, memory, memory.Id), new(new WorkflowAppUpdater()), preferences: prefs);
        try
        {
            await model.ReloadCommand.ExecuteAsync(null); model.Search = "Альфа"; await model.SearchDatabaseNowAsync();
            Assert.True(model.HasMoreOrders); Assert.Equal(100, model.Orders.Count);
            model.Search = "Бета"; Assert.False(model.HasMoreOrders);
            await model.LoadMoreOrdersCommand.ExecuteAsync(null);
            await model.SearchDatabaseNowAsync(); Assert.Equal(100, model.Orders.Count);
            Assert.All(model.Orders, row => Assert.Equal("Бета", row.Customer));
            await model.LoadMoreOrdersCommand.ExecuteAsync(null); Assert.Equal(120, model.Orders.Count); Assert.False(model.HasMoreOrders);
        }
        finally { model.Detach(); }
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task NewCatalogRefreshesOnlyInvalidCachedReferencesAndAllowsUnrelatedWrites(bool purged)
    {
        var memory = new MemoryMdb(); var book = WorkflowTestData.CreateWorkspace(); var at = DateTimeOffset.Now;
        var removed = Add(ref book, "Удалённая", at); book = Project(book, removed.Id, at);
        var other = Add(ref book, "Другая", at.AddMinutes(1)); memory.Book = book;
        var machine = book.Orders.Single(o => o.Id == removed.Id).Projects.Single().MachineId;
        var store = new WorkflowMdbStore(Path.Combine(Path.GetTempPath(), "cache-review.mdb"), memory, memory.Id);
        await store.ReadAsync(Token);
        var replacement = book.Machines.First(m => m.Id != machine);
        var updated = book.Orders.Single(o => o.Id == removed.Id) with
        {
            Version = removed.Version + 2,
            Projects = book.Orders.Single(o => o.Id == removed.Id).Projects.Select(p => p with
                { MachineId = replacement.Id, ImportProfile = replacement.ImportProfile }).ToArray()
        };
        memory.Book = book with
        {
            Orders = purged ? [other] : [updated, other],
            Machines = book.Machines.Where(m => m.Id != machine).ToArray()
        };
        await memory.SendAsync(new(1, WorkflowAccessOperation.Apply, store.FilePath, memory.Id, Patch: new(null, 0, WorkflowCatalog.From(memory.Book), [])), Token);
        memory.Requests.Clear();
        var result = await store.WriteOrderAsync(other.Id, other.Version, w => w, Token);
        Assert.Contains(result.Orders, o => o.Id == other.Id);
        if (purged) Assert.Single(result.Orders);
        else Assert.Equal(updated, result.Orders.Single(o => o.Id == removed.Id));
        Assert.DoesNotContain(memory.Requests, r => r.Operation == WorkflowAccessOperation.Apply);
        Assert.Contains(memory.Requests, r => r.Operation == WorkflowAccessOperation.ReadOrders && r.OrderIds!.SequenceEqual(new[] { removed.Id }));
    }
    [Fact]
    public async Task FailedCatalogReconciliationKeepsThePreviousSnapshotAndDoesNotWrite()
    {
        var memory = new MemoryMdb(); var book = WorkflowTestData.CreateWorkspace(); var at = DateTimeOffset.Now;
        var removed = Add(ref book, "Удалённая", at); book = Project(book, removed.Id, at);
        var other = Add(ref book, "Другая", at.AddMinutes(1)); memory.Book = book;
        var machine = book.Orders.Single(o => o.Id == removed.Id).Projects.Single().MachineId;
        var wire = new InterceptTransport(memory);
        var store = new WorkflowMdbStore(Path.Combine(Path.GetTempPath(), "repair-review.mdb"), wire, memory.Id);
        var original = await store.ReadAsync(Token);
        memory.Book = book with { Orders = [other], Machines = book.Machines.Where(m => m.Id != machine).ToArray() };
        await memory.SendAsync(new(1, WorkflowAccessOperation.Apply, store.FilePath, memory.Id, Patch: new(null, 0, WorkflowCatalog.From(memory.Book), [])), Token);
        wire.After = (request, response) => request.OrderIds?.Contains(removed.Id) == true
            ? throw new IOException("Synthetic reconciliation failure") : response;
        memory.Requests.Clear();
        await Assert.ThrowsAsync<IOException>(() => store.WriteOrderAsync(other.Id, other.Version, w => w, Token));
        Assert.DoesNotContain(memory.Requests, r => r.Operation == WorkflowAccessOperation.Apply);
        // No page is available, so this exposes the still-coherent cache without another database read.
        Assert.Same(original, await store.ReadMoreAsync(Token));
        wire.After = null;
        Assert.Single((await store.ReadOrderAsync(other.Id, Token)).Orders);
    }
    [Fact]
    public async Task InvalidReplyAfterCommittedWriteRequiresRefreshAndCannotReplayTheWrite()
    {
        var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() };
        var wire = new InterceptTransport(memory);
        var store = new WorkflowMdbStore(Path.Combine(Path.GetTempPath(), "committed-review.mdb"), wire, memory.Id);
        await store.ReadAsync(Token);
        wire.After = (request, response) => request.Operation == WorkflowAccessOperation.Apply ? response with { Orders = null } : response;
        await Assert.ThrowsAsync<WorkflowOutcomeUnknownException>(() => store.SaveEmployeeProfileAsync(null, "Новая фамилия", 0xFF628DB2, Token));
        Assert.True(store.RequiresRefresh); Assert.Contains(memory.Book.Employees, e => e.Name == "Новая фамилия");
        memory.Requests.Clear();
        await Assert.ThrowsAsync<WorkflowValidationException>(() => store.SaveEmployeeProfileAsync(null, "Новая фамилия", 0xFF628DB2, Token));
        Assert.Empty(memory.Requests);
        wire.After = null; await store.ReadAsync(Token); Assert.False(store.RequiresRefresh);
    }
    [Fact]
    public async Task LateReadCannotReinstateTheCursorOfAnObsoleteFilter()
    {
        var memory = new MemoryMdb(); var book = WorkflowTestData.CreateWorkspace(); var at = DateTimeOffset.Now;
        for (var i = 0; i < 3; ++i) Add(ref book, "Альфа", at.AddMinutes(i));
        memory.Book = book;
        var wire = new InterceptTransport(memory);
        var store = new WorkflowMdbStore(Path.Combine(Path.GetTempPath(), "late-page-review.mdb"), wire, memory.Id)
            { Query = new(Search: "Альфа", Limit: 1) };
        wire.After = (request, response) =>
        {
            if (request.Operation == WorkflowAccessOperation.Read) store.Query = new(Search: "Бета", Limit: 1);
            return response;
        };
        await store.ReadAsync(Token); Assert.False(store.HasMore); Assert.Equal(0, store.TotalCount);
        memory.Requests.Clear(); await store.ReadMoreAsync(Token); Assert.Empty(memory.Requests);
    }
    [Fact]
    public async Task CatalogChangeBetweenRenameChecksDoesNotMoveFilesOrLeaveAnIntent()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single();
        var proxy = DispatchProxy.Create<IWorkflowWorkspaceStore, BeforeWriteStore>(); var state = (BeforeWriteStore)(object)proxy;
        state.Inner = f.Store; state.Before = async () =>
        {
            var fresh = await f.Store.ReadAsync(Token);
            await f.Store.WriteAsync(fresh.Version, w => WorkflowChanges.UpdateCatalog(w,
                w.Machines.Select(m => m.Id == project.MachineId ? m with { ImportProfile = "hans-pdf/v1", ProjectExtension = ".Dsp" } : m).ToArray(), w.Rates), Token);
        };
        await Assert.ThrowsAsync<WorkflowConflictException>(() => new WorkflowProjectMaintenance(proxy).RenameAsync(order.Id, order.Version,
            project.Id, project.MachineId, 8, order.TechnologistId, Token));
        var after = await f.Store.ReadAsync(Token); Assert.Null(after.Orders.Single().Projects.Single().Rename);
        Assert.True(File.Exists(project.NativePath)); Assert.Equal(project.Name, after.Orders.Single().Projects.Single().Name);
    }
    [Fact]
    public async Task RenameAcceptsDatePrecisionLossWithoutChangingTheConfirmedDestination()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single();
        var proxy = DispatchProxy.Create<IWorkflowWorkspaceStore, BeforeWriteStore>(); var state = (BeforeWriteStore)(object)proxy;
        state.Inner = f.Store;
        state.After = w => w with { Orders = w.Orders.Select(o => o with
        {
            Projects = o.Projects.Select(p => p.Rename is { } plan ? p with
            { Rename = plan with { StartedAt = DateTimeOffset.FromUnixTimeSeconds(plan.StartedAt.ToUnixTimeSeconds()) } } : p).ToArray()
        }).ToArray() };
        var result = await new WorkflowProjectMaintenance(proxy).RenameAsync(order.Id, order.Version, project.Id,
            project.MachineId, 8, order.TechnologistId, Token);
        var renamed = result.Orders.Single().Projects.Single();
        Assert.Null(renamed.Rename); Assert.Equal(8, renamed.ThicknessMm); Assert.True(File.Exists(renamed.NativePath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(project.NativePath)));
    }
    public class BeforeWriteStore : DispatchProxy
    {
        public IWorkflowWorkspaceStore Inner { get; set; } = null!;
        public Func<Task>? Before { get; set; }
        public Func<WorkflowWorkspace, WorkflowWorkspace>? After { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IWorkflowWorkspaceStore.WriteOrderAsync) && Before is { } before)
            { Before = null; before().GetAwaiter().GetResult(); }
            try
            {
                var result = method.Invoke(Inner, args);
                return method.Name == nameof(IWorkflowWorkspaceStore.WriteOrderAsync) && After is { } after
                    ? Transform((Task<WorkflowWorkspace>)result!, after) : result;
            }
            catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
        }
        private static async Task<WorkflowWorkspace> Transform(Task<WorkflowWorkspace> result, Func<WorkflowWorkspace, WorkflowWorkspace> after)
            => after(await result);
    }
    private sealed class InterceptTransport(MemoryMdb memory) : IWorkflowAccessTransport
    {
        public Func<WorkflowAccessRequest, WorkflowAccessResponse, WorkflowAccessResponse>? After { get; set; }
        public async Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken token = default)
        {
            var response = await memory.SendAsync(request, token);
            return After?.Invoke(request, response) ?? response;
        }
    }
}
