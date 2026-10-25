using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

// The active employee belongs to this open store, not to whichever source another process selected on disk.
internal sealed record WorkflowConnection(IWorkflowWorkspaceStore Store, Guid? EmployeeId = null)
{
    public bool Matches(WorkflowPreferences preferences)
    {
        var path = Store is WorkflowMdbStore ? Store.FilePath : null;
        return string.Equals(path, preferences.DatabasePath is { } value ? Path.GetFullPath(value) : null,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
            (Store.DatabaseId is null && path is not null || Store.DatabaseId == preferences.DatabaseId);
    }

    public void RequireMatchingSource(WorkflowPreferences preferences)
    {
        if (!Matches(preferences))
            throw new WorkflowValidationException("Подключение изменено в другом окне. Этот сеанс продолжает работать с прежней базой. Подключите нужную MDB через настройки.");
    }
}
