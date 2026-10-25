using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public enum WorkflowAccessOperation { Create, Inspect, Read, ReadOrders, ReadTrash, Apply, Purge, ReadRevision }
public sealed record WorkflowCatalog(Employee[] Employees, Customer[] Customers, WorkflowMachine[] Machines,
    CuttingRate[] Rates, WorkflowPriceSource? PriceSource, string? ProjectRoot)
{
    public static WorkflowCatalog From(WorkflowWorkspace w) => new(w.Employees, w.Customers, w.Machines, w.Rates, w.PriceSource, w.ProjectRoot);
    public WorkflowWorkspace WithOrders(long version, WorkflowOrder[] orders) => new(11, version, Employees, Customers, Rates, orders)
    { Machines = Machines, PriceSource = PriceSource, ProjectRoot = ProjectRoot };
}
public sealed record WorkflowOrderReplacement(Guid Id, long? ExpectedVersion, WorkflowOrder? Value, bool AllowTrashed = false);
public sealed record WorkflowAccessPatch(long? ExpectedVersion, long ExpectedCatalogVersion,
    WorkflowCatalog? Catalog, WorkflowOrderReplacement[] Orders);
public sealed record WorkflowAccessRequest(int Protocol, WorkflowAccessOperation Operation, string Path,
    Guid? DatabaseId = null, WorkflowReadQuery? Query = null, Guid[]? OrderIds = null, WorkflowAccessPatch? Patch = null, long? KnownCatalogVersion = null,
    WorkflowTrashTarget[]? TrashTargets = null, Guid? ActorId = null);
public sealed record WorkflowAccessResponse(int Protocol, Guid DatabaseId = default, long CatalogVersion = 0, long Revision = 0,
    WorkflowCatalog? Catalog = null, WorkflowOrder[]? Orders = null, int TotalCount = 0, int TrashCount = 0, bool HasMore = false,
    string? Error = null, bool Conflict = false, bool OutcomeUnknown = false, Guid[]? PageIds = null);

public interface IWorkflowAccessTransport
{
    Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken token = default);
}
public sealed class WorkflowOutcomeUnknownException(Exception? inner = null) : IOException(
    "Не удалось подтвердить запись в MDB. Обновите журнал и проверьте результат; автоматически повторять запись нельзя.", inner);
