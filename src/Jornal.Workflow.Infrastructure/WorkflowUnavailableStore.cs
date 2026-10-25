using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

// An unreadable connection setting must never silently select a different writable database.
public sealed class WorkflowUnavailableStore(string path, string message) : IWorkflowWorkspaceStore
{
    public string FilePath => path;
    private Task<T> Fail<T>() => Task.FromException<T>(new WorkflowValidationException(message));
    public Task<WorkflowWorkspace> ReadAsync(CancellationToken token = default) => Fail<WorkflowWorkspace>();
    public Task<WorkflowWorkspace> WriteAsync(long expectedVersion, Func<WorkflowWorkspace, WorkflowWorkspace> change, CancellationToken token = default, bool backupBeforeWrite = false) => Fail<WorkflowWorkspace>();
    public Task<WorkflowWorkspace> WriteOrderAsync(Guid orderId, long expectedOrderVersion, Func<WorkflowWorkspace, WorkflowWorkspace> change, CancellationToken token = default, bool allowTrashed = false) => Fail<WorkflowWorkspace>();
    public Task<WorkflowWorkspace> SaveEmployeeProfileAsync(Employee? expected, string name, uint color, CancellationToken token = default) => Fail<WorkflowWorkspace>();
    public Task<WorkflowWorkspace> ArchiveTechnologistAsync(Employee expected, Guid actor, CancellationToken token = default) => Fail<WorkflowWorkspace>();
    public Task<WorkflowWorkspace> PurgeTrashedOrdersAsync(IReadOnlyList<WorkflowTrashTarget> targets, Guid actor, CancellationToken token = default) => Fail<WorkflowWorkspace>();
    public Task<(WorkflowWorkspace Workspace, Guid OrderId)> CreateOrderFromNamesAsync(string manager, string customer, Guid actor, DateTimeOffset at, CancellationToken token = default) => Fail<(WorkflowWorkspace, Guid)>();
    public Task<WorkflowWorkspace> WriteProjectRootAsync(string? expectedRoot, string? root, CancellationToken token = default) => Fail<WorkflowWorkspace>();
}
