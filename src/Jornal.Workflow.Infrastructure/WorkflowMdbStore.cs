using System.Text.Json;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed partial class WorkflowMdbStore(string path, IWorkflowAccessTransport? transport = null, Guid? databaseId = null) : IWorkflowQueryStore
{
    private readonly IWorkflowAccessTransport _transport = transport ?? new WorkflowAccessClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WorkflowCatalog? _catalog;
    private bool _inspected;
    private long _catalogVersion = -1;
    private WorkflowWorkspace _snapshot = WorkflowWorkspace.Empty;
    private WorkflowReadQuery _query = new();
    private WorkflowPageState _page = new(new());
    private WorkflowPageState _trashPage = new(new(Limit: 100));
    private Guid? _pendingCreate;
    private bool _unknownPurge;
    public string FilePath { get; } = NormalizePath(path);
    public Guid? DatabaseId { get; private set; } = databaseId;
    public WorkflowReadQuery Query
    {
        get => _query;
        set { _query = value; if (!_page.Matches(value)) _page = new(value); }
    }
    public bool HasMore => _page.HasMore;
    public bool HasMoreTrash => _trashPage.HasMore;
    public int TotalCount => _page.TotalCount;
    public int TrashCount { get; private set; }
    public bool RequiresRefresh { get; private set; }
    public Guid? RecoveredOrderId { get; private set; }
    public void AcknowledgeRecovery() { RecoveredOrderId = null; _pendingCreate = null; }
    public Task<WorkflowWorkspace> RecordShopHandoffAsync(Guid orderId, long expectedOrderVersion, Guid actor, DateTimeOffset at, CancellationToken token = default) => Locked(async () =>
    {
        if (RequiresRefresh) throw new WorkflowValidationException("Исход предыдущей записи не подтверждён. Сначала обновите журнал (F5).");
        try
        {
            var result = await _transport.SendAsync(new(1, WorkflowAccessOperation.RecordShopHandoff, FilePath, DatabaseId,
                ShopHandoff: new(orderId, expectedOrderVersion, actor, at), KnownCatalogVersion: _catalogVersion), token).ConfigureAwait(false);
            if (result.Orders?.SingleOrDefault(o => o.Id == orderId)?.ShopHandoff is not { } stamp ||
                stamp.OrderVersion != expectedOrderVersion || stamp.ActorId != actor || stamp.At != DateTimeOffset.FromUnixTimeSeconds(at.ToUnixTimeSeconds()))
                throw new WorkflowOutcomeUnknownException();
            return await MergeCommittedAsync(result, [orderId]).ConfigureAwait(false);
        }
        catch (WorkflowOutcomeUnknownException) { RequiresRefresh = true; throw; }
    }, token);
    public static string NormalizePath(string value)
    {
        if (!Path.IsPathFullyQualified(value) || !Path.GetExtension(value).Equals(".mdb", StringComparison.OrdinalIgnoreCase))
            throw new WorkflowValidationException("Выберите полный путь к MDB программы «Заявки».");
        return Path.GetFullPath(value);
    }
    public Task<WorkflowWorkspace> InspectAsync(CancellationToken token = default) => Locked(async () =>
    {
        var response = await Send(WorkflowAccessOperation.Inspect, token).ConfigureAwait(false);
        var result = await MergeAsync(response, token, reset: true).ConfigureAwait(false); _inspected = true; return result;
    }, token);
    public Task<WorkflowWorkspace> ReadAsync(CancellationToken token = default) => Locked(async () =>
    {
        if (!_inspected)
        {
            await MergeAsync(await Send(WorkflowAccessOperation.Inspect, token).ConfigureAwait(false), token, reset: true).ConfigureAwait(false);
            _inspected = true;
        }
        var page = _page;
        var query = page.Filter with { BeforeUpdated = null, BeforeNumber = null, RetainIds = Retained() };
        var response = await Send(WorkflowAccessOperation.Read, token, query: query).ConfigureAwait(false);
        var accepted = page.Accept(response);
        var result = await MergeAsync(response, token, reset: true).ConfigureAwait(false);
        if (ReferenceEquals(page, _page)) _page = accepted;
        RequiresRefresh = false; _unknownPurge = false;
        RecoveredOrderId = _pendingCreate is { } id && result.Orders.Any(o => o.Id == id) ? id : null;
        return result;
    }, token);
    public Task<WorkflowWorkspace> ReadMoreAsync(CancellationToken token = default) => Locked(async () =>
    {
        var page = _page;
        if (!page.HasMore || page.Cursor is not { } cursor) return _snapshot;
        var retained = Retained();
        var response = await Send(WorkflowAccessOperation.Read, token, query: page.Filter with { BeforeUpdated = cursor.Time, BeforeNumber = cursor.Number, RetainIds = retained }).ConfigureAwait(false);
        var accepted = page.Accept(response);
        var result = await MergeAsync(response, token, requested: retained).ConfigureAwait(false);
        if (ReferenceEquals(page, _page)) _page = accepted;
        return result;
    }, token);
    public Task<WorkflowWorkspace> ReadOrderAsync(Guid id, CancellationToken token = default) => Locked(async () =>
        await MergeAsync(await Send(WorkflowAccessOperation.ReadOrders, token, ids: [id]).ConfigureAwait(false), token, requested: [id]).ConfigureAwait(false), token);
    public Task<WorkflowWorkspace> ReadOrdersAsync(Guid[] ids, CancellationToken token = default) => Locked(async () =>
        await MergeAsync(await Send(WorkflowAccessOperation.ReadOrders, token, ids: ids).ConfigureAwait(false), token, requested: ids).ConfigureAwait(false), token);
    public Task<WorkflowWorkspace> ReadTrashAsync(CancellationToken token = default) => ReadTrash(false, token);
    public Task<WorkflowWorkspace> ReadMoreTrashAsync(CancellationToken token = default) => ReadTrash(true, token);
    private Task<WorkflowWorkspace> ReadTrash(bool more, CancellationToken token) => Locked(async () =>
    {
        var page = _trashPage;
        if (more && (!page.HasMore || page.Cursor is null)) return _snapshot;
        var retained = Retained();
        var response = await Send(WorkflowAccessOperation.ReadTrash, token, query: new(Limit: 100, RetainIds: Retained(),
            BeforeUpdated: more ? page.Cursor?.Time : null, BeforeNumber: more ? page.Cursor?.Number : null)).ConfigureAwait(false);
        var accepted = page.Accept(response);
        var replace = more ? retained : retained.Concat(_snapshot.Orders.Where(o => o.IsDeleted).Select(o => o.Id)).Distinct().ToArray();
        var result = await MergeAsync(response, token, requested: replace).ConfigureAwait(false); _trashPage = accepted;
        if (!more && _unknownPurge) { RequiresRefresh = false; _unknownPurge = false; }
        return result;
    }, token);
    public Task<WorkflowWorkspace> WriteAsync(long expectedVersion, Func<WorkflowWorkspace, WorkflowWorkspace> change, CancellationToken token = default, bool backupBeforeWrite = false) =>
        Write([], expectedVersion, change, token);
    public Task<WorkflowWorkspace> WriteOrderAsync(Guid orderId, long expectedOrderVersion, Func<WorkflowWorkspace, WorkflowWorkspace> change, CancellationToken token = default, bool allowTrashed = false) =>
        Write([orderId], null, book =>
        {
            var order = book.Orders.SingleOrDefault(o => o.Id == orderId);
            if (order?.Version != expectedOrderVersion) throw new WorkflowConflictException();
            if (order.IsDeleted && !allowTrashed) throw new WorkflowValidationException("Заявка находится в корзине.");
            return change(book);
        }, token, allowTrashed);
    public Task<WorkflowWorkspace> SaveEmployeeProfileAsync(Employee? expected, string name, uint color, CancellationToken token = default) =>
        Write([], null, w => WorkflowChanges.SaveEmployeeProfile(w, expected, name, color), token);
    public Task<WorkflowWorkspace> ArchiveTechnologistAsync(Employee expected, Guid actor, CancellationToken token = default) =>
        Write([], null, w => WorkflowChanges.ArchiveTechnologist(w, expected, actor), token);
    public Task<WorkflowWorkspace> WriteProjectRootAsync(string? expectedRoot, string? root, CancellationToken token = default) =>
        Write([], null, w =>
        {
            var next = WorkflowProjects.SetRoot(w, root);
            if (w.ProjectRoot != expectedRoot && w.ProjectRoot != next.ProjectRoot) throw new WorkflowConflictException();
            return next;
        }, token);
    public Task<WorkflowWorkspace> PurgeTrashedOrdersAsync(IReadOnlyList<WorkflowTrashTarget> targets, Guid actor, CancellationToken token = default) =>
        Purge(targets.ToArray(), actor, token);
    private Task<WorkflowWorkspace> Purge(WorkflowTrashTarget[] targets, Guid actor, CancellationToken token) => Locked(async () =>
    {
        if (RequiresRefresh) throw new WorkflowValidationException("Исход предыдущей записи не подтверждён. Сначала обновите журнал (F5).");
        if (targets.Length == 0) return _snapshot;
        try
        {
            var response = await _transport.SendAsync(new(1, WorkflowAccessOperation.Purge, FilePath, DatabaseId,
                KnownCatalogVersion: _catalog is null ? null : _catalogVersion, TrashTargets: targets, ActorId: actor), token).ConfigureAwait(false);
            return await MergeCommittedAsync(response, targets.Select(t => t.OrderId).ToArray()).ConfigureAwait(false);
        }
        catch (WorkflowOutcomeUnknownException) { RequiresRefresh = true; _unknownPurge = true; throw; }
    }, token);
    public async Task<(WorkflowWorkspace Workspace, Guid OrderId)> CreateOrderFromNamesAsync(string manager, string customer, Guid actor, DateTimeOffset at, CancellationToken token = default)
    {
        var id = _pendingCreate ?? Guid.NewGuid();
        var workspace = await Write([], null, w =>
        {
            var next = WorkflowChanges.CreateOrderFromNames(w, manager, customer, actor, at, out var generated);
            return next with { Orders = next.Orders.Select(o => o.Id == generated ? o with { Id = id } : o).ToArray() };
        }, token, creation: id).ConfigureAwait(false);
        _pendingCreate = null; return (workspace, id);
    }
    private Task<WorkflowWorkspace> Write(Guid[] ids, long? expectedVersion, Func<WorkflowWorkspace, WorkflowWorkspace> change, CancellationToken token, bool allowTrashed = false, Guid? creation = null) => Locked(async () =>
    {
        if (RequiresRefresh) throw new WorkflowValidationException("Исход предыдущей записи не подтверждён. Сначала обновите журнал (F5) и проверьте результат.");
        var read = await Send(WorkflowAccessOperation.ReadOrders, token, ids: ids).ConfigureAwait(false);
        await MergeAsync(read, token, requested: ids).ConfigureAwait(false);
        var before = _catalog!.WithOrders(read.Revision, read.Orders!);
        if (expectedVersion is { } expected && before.Version != expected) throw new WorkflowConflictException();
        var next = change(before); next.Validate();
        var catalog = SameCatalog(WorkflowCatalog.From(before), WorkflowCatalog.From(next)) ? null : WorkflowCatalog.From(next);
        var changes = before.Orders.Select(o => o.Id).Union(next.Orders.Select(o => o.Id)).Select(id =>
        {
            var old = before.Orders.SingleOrDefault(o => o.Id == id); var value = next.Orders.SingleOrDefault(o => o.Id == id);
            return SameOrder(old, value) ? null : new WorkflowOrderReplacement(id, old?.Version, value, allowTrashed);
        }).OfType<WorkflowOrderReplacement>().ToArray();
        if (catalog is null && changes.Length == 0) return _snapshot;
        var patch = new WorkflowAccessPatch(expectedVersion, read.CatalogVersion, catalog, changes);
        try
        {
            var result = await Send(WorkflowAccessOperation.Apply, token, patch: patch).ConfigureAwait(false);
            return await MergeCommittedAsync(result, changes.Select(c => c.Id).ToArray()).ConfigureAwait(false);
        }
        catch (WorkflowOutcomeUnknownException) { RequiresRefresh = true; if (creation is { } id) _pendingCreate = id; throw; }
    }, token);
    private static bool SameOrder(WorkflowOrder? a, WorkflowOrder? b) => ReferenceEquals(a, b) || a is not null && b is not null && JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);
    private static bool SameCatalog(WorkflowCatalog a, WorkflowCatalog b) =>
        Same(a.Employees, b.Employees) && Same(a.Customers, b.Customers) && Same(a.Machines, b.Machines) && Same(a.Rates, b.Rates) && a.ProjectRoot == b.ProjectRoot &&
        (ReferenceEquals(a.PriceSource, b.PriceSource) || JsonSerializer.Serialize(a.PriceSource) == JsonSerializer.Serialize(b.PriceSource));
    private static bool Same<T>(T[] a, T[] b) => ReferenceEquals(a, b) || a.SequenceEqual(b);
    private Guid[] Retained() => (Query.RetainIds ?? []).Concat(_pendingCreate is { } id ? new[] { id } : Array.Empty<Guid>()).Distinct().ToArray();
    private Task<WorkflowAccessResponse> Send(WorkflowAccessOperation operation, CancellationToken token, WorkflowReadQuery? query = null, Guid[]? ids = null, WorkflowAccessPatch? patch = null) =>
        _transport.SendAsync(new(1, operation, FilePath, DatabaseId, query, ids, patch, _catalog is null ? null : _catalogVersion), token);
    private async Task<WorkflowWorkspace> Locked(Func<Task<WorkflowWorkspace>> action, CancellationToken token)
    { await _gate.WaitAsync(token).ConfigureAwait(false); try { return await action().ConfigureAwait(false); } finally { _gate.Release(); } }
}
