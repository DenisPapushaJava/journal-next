using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed partial class WorkflowMdbStore
{
    private async Task<WorkflowWorkspace> MergeCommittedAsync(WorkflowAccessResponse response, Guid[] requested)
    {
        try { return await MergeAsync(response, CancellationToken.None, requested: requested).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { throw new WorkflowOutcomeUnknownException(ex); }
    }

    private async Task<WorkflowWorkspace> MergeAsync(WorkflowAccessResponse response, CancellationToken token,
        bool reset = false, Guid[]? requested = null)
    {
        ValidateResponse(response);
        var catalog = response.Catalog ?? (_catalogVersion == response.CatalogVersion ? _catalog : null) ??
            throw new InvalidDataException("Не передан актуальный каталог MDB.");
        catalog.WithOrders(response.Revision, response.Orders!).Validate();
        var replace = response.Orders!.Select(o => o.Id).Concat(requested ?? []).ToHashSet();
        var cached = reset ? [] : _snapshot.Orders.Where(o => !replace.Contains(o.Id)).ToArray();
        var fresh = new List<WorkflowOrder>(response.Orders!);
        var metadata = response;
        if (!reset && response.CatalogVersion != _catalogVersion)
        {
            var references = new CatalogReferences(catalog);
            var invalidIds = cached.Where(o => !references.Contains(o)).Select(o => o.Id).ToArray();
            // Another client can purge a cached order and then remove its unused catalog entries.
            // Re-read only affected IDs: a missing order is evicted; a changed order is refreshed.
            foreach (var ids in invalidIds.Chunk(512))
            {
                var repaired = await _transport.SendAsync(new(1, WorkflowAccessOperation.ReadOrders, FilePath,
                    response.DatabaseId, OrderIds: ids, KnownCatalogVersion: response.CatalogVersion), token).ConfigureAwait(false);
                ValidateResponse(repaired);
                if (repaired.DatabaseId != response.DatabaseId || repaired.CatalogVersion != response.CatalogVersion)
                    throw new WorkflowConflictException();
                if (repaired.Orders!.Any(o => !ids.Contains(o.Id))) throw new InvalidDataException("MDB вернула незапрошенные заявки.");
                catalog.WithOrders(repaired.Revision, repaired.Orders!).Validate();
                replace.UnionWith(ids); fresh.AddRange(repaired.Orders!);
                if (repaired.Revision >= metadata.Revision) metadata = repaired;
            }
        }
        token.ThrowIfCancellationRequested();
        var workspace = catalog.WithOrders(metadata.Revision, cached.Where(o => !replace.Contains(o.Id)).Concat(fresh).ToArray());
        workspace.Validate();
        // Publish the coherent snapshot only after every dependent read and validation succeeds.
        DatabaseId = response.DatabaseId; _catalog = catalog; _catalogVersion = response.CatalogVersion;
        _snapshot = workspace; TrashCount = metadata.TrashCount;
        return workspace;
    }

    private void ValidateResponse(WorkflowAccessResponse response)
    {
        if (response.Protocol != 1 || response.Error is not null || response.DatabaseId == Guid.Empty || response.Orders is null ||
            response.Revision < 0 || response.CatalogVersion < 0 || DatabaseId is { } expected && expected != response.DatabaseId ||
            response.Orders.Select(o => o.Id).Distinct().Count() != response.Orders.Length)
            throw new InvalidDataException("Некорректный снимок MDB.");
    }

    private sealed class CatalogReferences(WorkflowCatalog catalog)
    {
        private readonly HashSet<Guid> _employees = catalog.Employees.Select(e => e.Id).ToHashSet();
        private readonly HashSet<Guid> _customers = catalog.Customers.Select(c => c.Id).ToHashSet();
        private readonly HashSet<Guid> _machines = catalog.Machines.Select(m => m.Id).ToHashSet();
        private bool Employee(Guid? id) => id is null || _employees.Contains(id.Value);
        public bool Contains(WorkflowOrder order) => _customers.Contains(order.CustomerId) &&
            Employee(order.ManagerId) && Employee(order.TechnologistId) && Employee(order.CreatedById) && Employee(order.DeletedBy) &&
            order.Projects.All(p => _machines.Contains(p.MachineId) && Employee(p.DeletedBy) &&
                (p.Rename is not { } rename || _machines.Contains(rename.MachineId) && Employee(rename.ActorId))) &&
            order.Specifications.All(s => _machines.Contains(s.Current.Parameters.MachineId) && Employee(s.Current.AuthorId));
    }
}
