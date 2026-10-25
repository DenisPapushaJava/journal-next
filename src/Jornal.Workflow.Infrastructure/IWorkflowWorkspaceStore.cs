using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public interface IWorkflowWorkspaceStore
{
    string FilePath { get; }
    Guid? DatabaseId => null;
    Task<WorkflowWorkspace> ReadAsync(CancellationToken token = default);
    Task<WorkflowWorkspace> ReadOrderAsync(Guid id, CancellationToken token = default) => ReadAsync(token);
    Task<WorkflowWorkspace> ReadTrashAsync(CancellationToken token = default) => ReadAsync(token);
    Task<WorkflowWorkspace> WriteAsync(long expectedVersion, Func<WorkflowWorkspace, WorkflowWorkspace> change, CancellationToken token = default, bool backupBeforeWrite = false);
    Task<WorkflowWorkspace> WriteOrderAsync(Guid orderId, long expectedOrderVersion, Func<WorkflowWorkspace, WorkflowWorkspace> change, CancellationToken token = default, bool allowTrashed = false);
    Task<WorkflowWorkspace> SaveEmployeeProfileAsync(Employee? expected, string name, uint color, CancellationToken token = default);
    Task<WorkflowWorkspace> ArchiveTechnologistAsync(Employee expected, Guid actor, CancellationToken token = default);
    Task<WorkflowWorkspace> PurgeTrashedOrdersAsync(IReadOnlyList<WorkflowTrashTarget> targets, Guid actor, CancellationToken token = default);
    Task<(WorkflowWorkspace Workspace, Guid OrderId)> CreateOrderFromNamesAsync(string manager, string customer, Guid actor, DateTimeOffset at, CancellationToken token = default);
    Task<WorkflowWorkspace> WriteProjectRootAsync(string? expectedRoot, string? root, CancellationToken token = default);
}

public sealed record WorkflowReadQuery(DateTimeOffset? From = null, DateTimeOffset? Until = null, string Search = "", int Limit = 100,
    DateTimeOffset? BeforeUpdated = null, long? BeforeNumber = null, Guid[]? RetainIds = null);

public interface IWorkflowQueryStore : IWorkflowWorkspaceStore
{
    WorkflowReadQuery Query { get; set; }
    bool HasMore { get; }
    int TotalCount { get; }
    int TrashCount { get; }
    bool HasMoreTrash { get; }
    Task<WorkflowWorkspace> ReadMoreAsync(CancellationToken token = default);
    Task<WorkflowWorkspace> ReadMoreTrashAsync(CancellationToken token = default);
}
