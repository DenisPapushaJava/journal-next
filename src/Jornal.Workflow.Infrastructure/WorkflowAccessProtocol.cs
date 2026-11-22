using System.Text.Json.Serialization;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public enum WorkflowAccessOperation { Create, Inspect, Read, ReadOrders, ReadTrash, Apply, Purge, ReadRevision, RecordShopHandoff, ClearShopHandoff, RecordProjectHandoffs, ReadOfficeOrders, ReadOfficeRevision, ReadOfficeProjects, ReadOfficeTechnologists }
public sealed record WorkflowOfficeOrder(Guid Id, string Number, string? Customer, long Version, DateTimeOffset UpdatedAt);
public sealed record WorkflowOfficeProject(Guid Id, string Name, string NativePath);
public sealed record WorkflowOfficeReportInput(string FileName, string Sha256, ReportValues Baseline);
public sealed record WorkflowOfficeReportVersion(Guid ProjectId, string Sha256);
public sealed record WorkflowOfficeCalculationPreparation(long OrderVersion, Guid ActorId, WorkflowOfficeReportInput? Report, bool EnsureBending);
public sealed record WorkflowOfficePreparedCalculation(string? SourcePath, AcceptedReport? Report);
public sealed record WorkflowOfficeProjectPreparation(Guid ProjectId, long OrderVersion, Guid ActorId, Guid MachineId, decimal ThicknessMm, Guid? ItemId);
public sealed record WorkflowOfficeTechnologist(Guid Id, string Name);
public sealed record WorkflowOfficeTechnologists(Guid DatabaseId, WorkflowOfficeTechnologist[] Technologists);
public sealed record WorkflowOfficeSession(Guid DatabaseId, WorkflowOfficeTechnologist Technologist, bool CanWrite, string? Token = null,
    string? ReadOnlyReason = null);
public sealed record WorkflowShopHandoffClearRequest(Guid OrderId, long ExpectedOrderVersion, Guid ActorId, WorkflowHandoffClearExpected Expected);
public sealed record WorkflowProjectHandoffRequest(Guid OrderId, long ExpectedOrderVersion, Guid[] ProjectIds, Guid ActorId, DateTimeOffset At);
public sealed record WorkflowShopHandoffRequest(Guid OrderId, long ExpectedOrderVersion, Guid ActorId, DateTimeOffset At);
public sealed record WorkflowCatalog(Employee[] Employees, Customer[] Customers, WorkflowMachine[] Machines,
    CuttingRate[] Rates, WorkflowPriceSource? PriceSource, string? ProjectRoot)
{
    public static WorkflowCatalog From(WorkflowWorkspace w) => new(w.Employees, w.Customers, w.Machines, w.Rates, w.PriceSource, w.ProjectRoot);
    public WorkflowWorkspace WithOrders(long version, WorkflowOrder[] orders) => new(WorkflowWorkspace.CurrentSchema, version, Employees, Customers, Rates, orders)
    { Machines = Machines, PriceSource = PriceSource, ProjectRoot = ProjectRoot };
}
public sealed record WorkflowOrderReplacement(Guid Id, long? ExpectedVersion, WorkflowOrder? Value, bool AllowTrashed = false);
public sealed record WorkflowAccessPatch(long? ExpectedVersion, long ExpectedCatalogVersion,
    WorkflowCatalog? Catalog, WorkflowOrderReplacement[] Orders)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowOfficeReportVersion[]? OfficeReportVersions { get; init; }
}
public sealed record WorkflowAccessRequest(int Protocol, WorkflowAccessOperation Operation, string Path,
    Guid? DatabaseId = null, WorkflowReadQuery? Query = null, Guid[]? OrderIds = null, WorkflowAccessPatch? Patch = null, long? KnownCatalogVersion = null,
    WorkflowTrashTarget[]? TrashTargets = null, Guid? ActorId = null, WorkflowShopHandoffRequest? ShopHandoff = null, WorkflowShopHandoffClearRequest? ClearShopHandoff = null, WorkflowProjectHandoffRequest? ProjectHandoffs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool OfficeSchema6 = false);
public sealed record WorkflowAccessResponse(int Protocol, Guid DatabaseId = default, long CatalogVersion = 0, long Revision = 0,
    WorkflowCatalog? Catalog = null, WorkflowOrder[]? Orders = null, int TotalCount = 0, int TrashCount = 0, bool HasMore = false,
    string? Error = null, bool Conflict = false, bool OutcomeUnknown = false, Guid[]? PageIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkflowOfficeOrder[]? OfficeOrders = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OfficeSchemaVersion = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkflowOfficeProject[]? OfficeProjects = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorkflowOfficeTechnologist[]? OfficeTechnologists = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? OfficeSchemaIsCurrent = null);

public interface IWorkflowAccessTransport
{
    Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken token = default);
}
public sealed class WorkflowOutcomeUnknownException(Exception? inner = null) : IOException(
    "Не удалось подтвердить запись в MDB. Обновите журнал и проверьте результат; автоматически повторять запись нельзя.", inner);
