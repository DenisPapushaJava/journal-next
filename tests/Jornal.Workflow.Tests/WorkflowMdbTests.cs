using System.Text.Json;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

// A managed transport exercises the real store/UI contract. It does not stand in for native Jet tests.
internal sealed class MemoryMdb : IWorkflowAccessTransport, IWorkflowDatabaseService
{
    public Guid Id { get; } = Guid.NewGuid();
    public WorkflowWorkspace Book { get; set; } = WorkflowWorkspace.Empty;
    public long CatalogVersion { get; private set; }
    public List<WorkflowAccessRequest> Requests { get; } = [];
    public bool LoseNextWriteReply { get; set; }
    public bool FailOpen { get; set; }
    public bool IsAvailable => true;
    public async Task<IWorkflowWorkspaceStore> OpenAsync(string path, bool create, CancellationToken token = default)
    {
        if (FailOpen) throw new WorkflowValidationException("Файл недоступен");
        if (create) await SendAsync(new(1, WorkflowAccessOperation.Create, path), token);
        var store = new WorkflowMdbStore(path, this, Id); await store.InspectAsync(token); return store;
    }
    public Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest r, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); Requests.Add(r);
        if (r.DatabaseId is { } id && id != Id) throw new WorkflowValidationException("Другая база");
        var ids = r.OrderIds ?? []; Guid[] page = []; bool more = false; int total = 0;
        if (r.Operation == WorkflowAccessOperation.Purge)
        {
            Book = WorkflowChanges.PurgeTrashedOrders(Book, r.TrashTargets!, r.ActorId!.Value) with { Version = Book.Version + 1 };
            if (LoseNextWriteReply) { LoseNextWriteReply = false; throw new WorkflowOutcomeUnknownException(); }
        }
        if (r.Operation == WorkflowAccessOperation.Apply)
        {
            var p = r.Patch!;
            if (p.ExpectedCatalogVersion != CatalogVersion || p.ExpectedVersion is { } expected && expected != Book.Version) throw new WorkflowConflictException();
            var orders = Book.Orders.ToList();
            foreach (var change in p.Orders)
            {
                var old = orders.SingleOrDefault(o => o.Id == change.Id);
                if (old?.Version != change.ExpectedVersion) throw new WorkflowConflictException();
                orders.RemoveAll(o => o.Id == change.Id);
                if (change.Value is { } next)
                {
                    if (old is null) next = next with
                    {
                        Number = orders.Select(o => o.Number).DefaultIfEmpty().Max() + 1,
                        ReferenceNumber = WorkflowOrderNumbers.Allocate(orders.Select(o => o.ReferenceNumber).ToHashSet(StringComparer.Ordinal), next.CreatedAt)
                    };
                    orders.Add(next);
                }
            }
            Book = (p.Catalog ?? WorkflowCatalog.From(Book)).WithOrders(Book.Version + 1, orders.ToArray()); Book.Validate();
            if (p.Catalog is not null) CatalogVersion++;
            if (LoseNextWriteReply) { LoseNextWriteReply = false; throw new WorkflowOutcomeUnknownException(); }
            ids = p.Orders.Select(o => o.Id).ToArray();
        }
        if (r.Operation is WorkflowAccessOperation.Read or WorkflowAccessOperation.ReadTrash)
        {
            var q = r.Query ?? new(); var search = new WorkflowSearchQuery(q.Search); var index = new WorkflowOrderSearchIndex(Book);
            var candidates = Book.Orders.Where(o => o.IsDeleted == (r.Operation == WorkflowAccessOperation.ReadTrash) &&
                (q.From is null || o.CreatedAt >= q.From) && (q.Until is null || o.CreatedAt < q.Until) && index.CreateDocument(o).Matches(search, out _)).ToArray();
            total = candidates.Length;
            candidates = candidates.Where(o => q.BeforeUpdated is null || o.UpdatedAt < q.BeforeUpdated || o.UpdatedAt == q.BeforeUpdated && o.Number < q.BeforeNumber)
                .OrderByDescending(o => o.UpdatedAt).ThenByDescending(o => o.Number).ToArray();
            more = candidates.Length > q.Limit; page = candidates.Take(q.Limit).Select(o => o.Id).ToArray(); ids = page.Concat(q.RetainIds ?? []).Distinct().ToArray();
        }
        return Task.FromResult(new WorkflowAccessResponse(1, Id, CatalogVersion, Book.Version,
            r.KnownCatalogVersion == CatalogVersion ? null : WorkflowCatalog.From(Book), Book.Orders.Where(o => ids.Contains(o.Id)).ToArray(),
            total, Book.Orders.Count(o => o.IsDeleted), more, PageIds: page));
    }
}

public sealed class WorkflowMdbTests
{
    private static string TempMdb() => Path.Combine(Path.GetTempPath(), "JournalNext-" + Guid.NewGuid().ToString("N") + ".mdb");
    private static readonly DateTimeOffset At = new(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(3));
    [Fact]
    public async Task RelationalRoundtripPreservesProjectCalculationReportAndAllFlags()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        f.Model.SelectedDraft!.HasBending = true; f.Model.SelectedDraft.HasChamfer = true; f.Model.SelectedDraft.Comment = "Сохранить точно";
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var flat = WorkflowMdbMapping.Flatten(book);
        var orders = WorkflowMdbMapping.Orders(flat);
        var roundtrip = WorkflowMdbMapping.Flatten(book with { Orders = orders });
        foreach (var table in flat)
            foreach (var row in table.Value) Assert.True(WorkflowMdbMapping.Same(row.Value, roundtrip[table.Key][row.Key]), table.Key);
        Assert.Equal(book.Employees.OrderBy(e => e.Id), flat["employees"].Values.Select(WorkflowMdbMapping.Read<Employee>).OrderBy(e => e.Id));
        Assert.All(flat.Values.SelectMany(t => t.Values).SelectMany(r => r.Values.Values), value =>
            Assert.True(value is null or string or Guid or bool or int or decimal or DateTime));
        Assert.DoesNotContain(flat.Values.SelectMany(t => t.Values).SelectMany(r => r.Values.Keys), n => n.Contains("json", StringComparison.OrdinalIgnoreCase));
        flat["order_item_selections"].Clear();
        Assert.Throws<WorkflowValidationException>(() => WorkflowMdbMapping.Orders(flat));
    }
    [Theory]
    [InlineData("0.12345678901")]
    [InlineData("1000000000000000000")]
    public void ValuesAreNeverSilentlyRoundedToMdbPrecision(string text)
    {
        var column = new WorkflowMdbColumn("price", "Price", WorkflowMdbType.Decimal);
        Assert.Throws<WorkflowValidationException>(() => WorkflowMdbMapping.ToDatabase(column, JsonSerializer.SerializeToNode(decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture))));
    }
    [Fact]
    public void SchemaUsesOwnIdentityForeignKeysAndUniqueBusinessNumbers()
    {
        var sql = WorkflowMdbSchema.CreateSql().ToArray();
        Assert.Equal(WorkflowMdbSchema.Tables.Length + 1, sql.Count(s => s.StartsWith("CREATE TABLE")));
        Assert.Contains(WorkflowMdbSchema.Indexes, i => i.Table == "orders" && i.Unique && i.Columns.SequenceEqual(new[] { "reference_number" }));
        Assert.Contains(WorkflowMdbSchema.ForeignKeys, f => f.Name == "fk_item_current" && f.Table == "order_item_selections" && f.Columns.SequenceEqual(new[] { "id", "project_id" }));
        Assert.All(WorkflowMdbSchema.Tables.Single(t => t.Name == "order_item_selections").Columns, c => Assert.True(c.Required));
        Assert.All(WorkflowMdbSchema.ForeignKeys, f => Assert.Contains(WorkflowMdbSchema.Tables, t => t.Name == f.Parent));
        Assert.DoesNotContain(sql, s => s.Contains("CASCADE", StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void SearchIsBoundedParameterizedLiteralAndUsesStableSeekCursor()
    {
        const string search = "Ст3 O'Reilly_*%";
        var plan = WorkflowMdbQueries.Build(new(At, At.AddDays(1), search, 20, At, 12));
        Assert.DoesNotContain("O'Reilly", plan.SelectIds); Assert.DoesNotContain("Ст3", plan.SelectIds);
        Assert.Equal(plan.SelectIds.Count(c => c == '?'), plan.Parameters.Length);
        Assert.Equal(plan.Count.Count(c => c == '?'), plan.CountParameters.Length);
        Assert.Contains(plan.Parameters, p => Equals(p.Value, "O'REILLY_*%")); Assert.Contains("TOP 21", plan.SelectIds);
        Assert.Contains("o.[updated_at]=? AND o.[order_number]<?", plan.SelectIds);
        Assert.DoesNotContain("ORDER BY", plan.Count);
        Assert.Throws<WorkflowValidationException>(() => WorkflowMdbQueries.Build(new(Limit: 10000)));
        Assert.Throws<WorkflowValidationException>(() => WorkflowMdbQueries.Build(new(Search: "1 2 3 4 5 6 7 8 9")));
    }
    [Fact]
    public async Task StoreCachesCatalogAndWritesOnlyTheTargetOrderWithExpectedVersion()
    {
        var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() }; var store = new WorkflowMdbStore(TempMdb(), memory);
        await store.InspectAsync(TestContext.Current.CancellationToken); var actor = memory.Book.Employees[0].Id;
        var first = await store.CreateOrderFromNamesAsync("Менеджер", "Клиент", actor, At, TestContext.Current.CancellationToken);
        var second = await store.CreateOrderFromNamesAsync("Менеджер", "Клиент 2", actor, At, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.OrderId, second.OrderId); Assert.Equal(2, memory.Book.Orders.Select(o => o.ReferenceNumber).Distinct().Count());
        memory.Requests.Clear(); var current = memory.Book.Orders.Single(o => o.Id == first.OrderId);
        await store.WriteOrderAsync(current.Id, current.Version, w => WorkflowChanges.UpdateOrderFromManagerName(w, current.Id, current.Version, "Менеджер", actor, "БЛЦБ-7", actor, At.AddMinutes(1)), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { WorkflowAccessOperation.ReadOrders, WorkflowAccessOperation.Apply }, memory.Requests.Select(r => r.Operation));
        Assert.Equal(new[] { current.Id }, memory.Requests[0].OrderIds);
        Assert.NotNull(memory.Requests[0].KnownCatalogVersion); Assert.Null(memory.Requests[1].Patch!.Catalog);
        Assert.Equal(current.Id, Assert.Single(memory.Requests[1].Patch!.Orders).Id);
        Assert.Equal(current.Version, memory.Requests[1].Patch!.Orders[0].ExpectedVersion);
        var requests = memory.Requests.Count;
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.WriteOrderAsync(current.Id, current.Version, w => w, TestContext.Current.CancellationToken));
        Assert.Equal(requests + 1, memory.Requests.Count); // Reads once; never replays the conflicting write.
    }
    [Fact]
    public async Task UnknownCreateOutcomeIsNotRetriedAndF5RecoversTheOriginalId()
    {
        var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace(), LoseNextWriteReply = true };
        var store = new WorkflowMdbStore(TempMdb(), memory); var actor = memory.Book.Employees[0].Id;
        await Assert.ThrowsAsync<WorkflowOutcomeUnknownException>(() => store.CreateOrderFromNamesAsync("Менеджер", "Клиент", actor, At, TestContext.Current.CancellationToken));
        Assert.True(store.RequiresRefresh); var id = Assert.Single(memory.Book.Orders).Id; var count = memory.Requests.Count;
        await Assert.ThrowsAsync<WorkflowValidationException>(() => store.CreateOrderFromNamesAsync("Менеджер", "Клиент", actor, At, TestContext.Current.CancellationToken));
        Assert.Equal(count, memory.Requests.Count);
        await store.ReadAsync(TestContext.Current.CancellationToken); Assert.False(store.RequiresRefresh); Assert.Equal(id, store.RecoveredOrderId); Assert.Single(memory.Book.Orders);
    }
    [Fact]
    public async Task PagingRetainsOpenCardsWithoutLoadingEveryOrderAndNoOpDoesNotWrite()
    {
        var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() }; var actor = memory.Book.Employees[0].Id;
        for (var i = 0; i < 7; i++) memory.Book = WorkflowChanges.CreateOrderFromNames(memory.Book, "Менеджер", "Клиент", actor, At.AddMinutes(i), out _);
        var store = new WorkflowMdbStore(TempMdb(), memory) { Query = new(Limit: 2, RetainIds: [memory.Book.Orders.OrderBy(o => o.UpdatedAt).First().Id]) };
        var page = await store.ReadAsync(TestContext.Current.CancellationToken); Assert.Equal(3, page.Orders.Length); Assert.True(store.HasMore); Assert.Equal(7, store.TotalCount);
        page = await store.ReadMoreAsync(TestContext.Current.CancellationToken); Assert.Equal(5, page.Orders.Length);
        while (store.HasMore) page = await store.ReadMoreAsync(TestContext.Current.CancellationToken); Assert.Equal(7, page.Orders.Length);
        memory.Requests.Clear(); await store.WriteOrderAsync(page.Orders[0].Id, page.Orders[0].Version, w => w, TestContext.Current.CancellationToken);
        Assert.Single(memory.Requests); Assert.Equal(WorkflowAccessOperation.ReadOrders, memory.Requests[0].Operation);
    }
}

public sealed class WorkflowMdbEdgeTests
{
    [Fact]
    public void ImportedRateIdentityDoesNotDependOnDecimalScaleAfterJetReadback()
    {
        WorkflowWorkspace WithThickness(decimal value) => WorkflowWorkspace.Empty with
        { PriceSource = new("prices.mdb", DateTimeOffset.UtcNow, [new("Лазер", "Базовая", value)]) };
        var a = WorkflowMdbMapping.Flatten(WithThickness(6m))["price_import_keys"];
        var b = WorkflowMdbMapping.Flatten(WithThickness(6.0000000000m))["price_import_keys"];
        Assert.Equal(a.Keys, b.Keys); Assert.True(WorkflowMdbMapping.Same(a.Values.Single(), b.Values.Single()));
    }
    [Fact]
    public void RestartLetterIsNotConsumedByAnotherDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Next-restart-db-" + Guid.NewGuid().ToString("N"));
        try
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid(); var store = new WorkflowRestartStore(Path.Combine(directory, "restart.json"));
            var draft = new WorkflowRestartDraft(1, null, "Письмо базы A") { DatabaseId = a };
            store.Save(draft); Assert.Null(store.Take(b)); Assert.Null(store.Take());
            Assert.Equal(draft, store.Take(a)); Assert.Null(store.Take(a));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task TrashedOrdersArePagedAndClearAllNeverMeansOnlyTheFirstPage()
    {
        var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() }; var actor = memory.Book.Employees[0].Id;
        for (var i = 0; i < 102; i++)
        {
            var at = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero).AddMinutes(i);
            memory.Book = WorkflowChanges.CreateOrderFromNames(memory.Book, "Менеджер", "Клиент", actor, at, out var id);
            var order = memory.Book.Orders.Single(o => o.Id == id);
            memory.Book = WorkflowChanges.MoveOrderToTrash(memory.Book, id, order.Version, actor, at);
        }
        var store = new WorkflowMdbStore(Path.Combine(Path.GetTempPath(), "trash-test.mdb"), memory);
        var book = await store.ReadTrashAsync(TestContext.Current.CancellationToken);
        var model = new WorkflowTrashViewModel(() => book, store.ReadTrashAsync, (_, _, _) => Task.FromResult<string?>(null),
            (_, _) => throw new InvalidOperationException(),
            async token => book = await store.ReadMoreTrashAsync(token), () => store.HasMoreTrash, () => store.TrashCount);
        Assert.True(model.HasMore); Assert.Equal(100, model.Orders.Count); Assert.False(model.CanClearTrash);
        Assert.Null(model.CreatePurgeConfirmation(true));
        await model.LoadMoreCommand.ExecuteAsync(null);
        Assert.False(model.HasMore); Assert.Equal(102, model.Orders.Count); Assert.True(model.CanClearTrash);
    }
}

public sealed class WorkflowMdbBulkPurgeTests
{
    [Fact]
    public async Task BulkPurgeUsesOneFrozenHeaderRequestAndRejectsEveryDeletionIfOneVersionIsStale()
    {
        var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() };
        var actor = memory.Book.Employees[0].Id;
        for (var i = 0; i < 601; i++)
        {
            var at = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero).AddMinutes(i);
            memory.Book = WorkflowChanges.CreateOrderFromNames(memory.Book, "Менеджер", "Клиент", actor, at, out var id);
            memory.Book = WorkflowChanges.MoveOrderToTrash(memory.Book, id, memory.Book.Orders.Single(o => o.Id == id).Version, actor, at);
        }
        var frozen = memory.Book.Orders.Select(o => new WorkflowTrashTarget(o.Id, o.Version)).ToArray();
        var store = new WorkflowMdbStore(Path.Combine(Path.GetTempPath(), "bulk.mdb"), memory);
        await store.ReadTrashAsync(TestContext.Current.CancellationToken); memory.Requests.Clear();
        var stale = frozen.ToArray(); stale[^1] = stale[^1] with { Version = 0 };
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.PurgeTrashedOrdersAsync(stale, actor, TestContext.Current.CancellationToken));
        Assert.Equal(601, memory.Book.Orders.Length); Assert.Single(memory.Requests);
        Assert.Equal(WorkflowAccessOperation.Purge, memory.Requests[0].Operation);
        memory.Requests.Clear();
        var book = await store.PurgeTrashedOrdersAsync(frozen, actor, TestContext.Current.CancellationToken);
        Assert.Empty(book.Orders); Assert.Empty(memory.Book.Orders); Assert.Equal(0, store.TrashCount);
        var request = Assert.Single(memory.Requests); Assert.Equal(frozen, request.TrashTargets);
        Assert.Null(request.Patch); Assert.Null(request.OrderIds);
    }
}

public sealed class WorkflowMdbPurgeRecoveryTests
{
    [Fact]
    public async Task ExplicitTrashRefreshResolvesUnknownPurgeWithoutReplayingTheRequest()
    {
        var memory = new MemoryMdb { Book = WorkflowTestData.CreateWorkspace() };
        var actor = memory.Book.Employees[0].Id; var at = DateTimeOffset.Now;
        memory.Book = WorkflowChanges.CreateOrderFromNames(memory.Book, "Менеджер", "Клиент", actor, at, out var id);
        memory.Book = WorkflowChanges.MoveOrderToTrash(memory.Book, id, 1, actor, at);
        var order = memory.Book.Orders.Single(); var store = new WorkflowMdbStore(Path.Combine(Path.GetTempPath(), "purge-recovery.mdb"), memory);
        await store.ReadTrashAsync(TestContext.Current.CancellationToken); memory.LoseNextWriteReply = true;
        await Assert.ThrowsAsync<WorkflowOutcomeUnknownException>(() => store.PurgeTrashedOrdersAsync([new(id, order.Version)], actor, TestContext.Current.CancellationToken));
        Assert.True(store.RequiresRefresh); Assert.Empty(memory.Book.Orders);
        await store.ReadTrashAsync(TestContext.Current.CancellationToken);
        Assert.False(store.RequiresRefresh); Assert.Equal(0, store.TrashCount);
        Assert.Single(memory.Requests, r => r.Operation == WorkflowAccessOperation.Purge);
    }
}
