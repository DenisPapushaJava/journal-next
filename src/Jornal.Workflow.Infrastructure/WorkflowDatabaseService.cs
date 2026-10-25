namespace Jornal.Workflow.Infrastructure;

public interface IWorkflowDatabaseService
{
    bool IsAvailable { get; }
    Task<IWorkflowWorkspaceStore> OpenAsync(string path, bool create, CancellationToken token = default);
}

public sealed class WorkflowDatabaseService(IWorkflowAccessTransport? transport = null) : IWorkflowDatabaseService
{
    public bool IsAvailable => OperatingSystem.IsWindows();
    public async Task<IWorkflowWorkspaceStore> OpenAsync(string path, bool create, CancellationToken token = default)
    {
        var client = transport ?? new WorkflowAccessClient();
        path = WorkflowMdbStore.NormalizePath(path);
        Guid? id = null;
        if (create)
        {
            try
            {
                var response = await client.SendAsync(new(1, WorkflowAccessOperation.Create, path), token).ConfigureAwait(false);
                id = response.DatabaseId;
            }
            catch (WorkflowOutcomeUnknownException)
            { throw new Core.WorkflowValidationException("Не удалось подтвердить создание MDB. Проверьте выбранный файл; если он появился, выберите его через «Подключить БД…». Существующий файл не перезаписывается."); }
        }
        var store = new WorkflowMdbStore(path, client, id);
        await store.InspectAsync(token).ConfigureAwait(false);
        return store;
    }
}
