namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private (WorkflowOfficeStorageClient Client, WorkflowOfflineFolders Folders)? _offlineOfficeFolders;

    public bool HasOfflineOfficeFolders => OfflineOfficeFolders is { Items.Length: > 0 };

    private WorkflowOfflineFolders? OfflineOfficeFolders => !_hasLoadedOrders && _offlineOfficeFolders is { } offline &&
        ReferenceEquals(offline.Client, _connection.RemoteClient) ? offline.Folders : null;

    private void ClearOfflineOfficeFolders()
    {
        if (_offlineOfficeFolders is null) return;
        _offlineOfficeFolders = null;
        OnPropertyChanged(nameof(HasOfflineOfficeFolders));
    }

    private async Task<WorkflowOfflineFolders?> ReadOfflineOfficeFoldersAsync(WorkflowOfficeStorageClient client, CancellationToken token)
    {
        var folders = await Task.Run(() => client.ListDownloadedFolders(token), token);
        if (_cuttingTaskDetached || token.IsCancellationRequested || _hasLoadedOrders || !ReferenceEquals(client, _connection.RemoteClient))
            return null;
        _offlineOfficeFolders = (client, folders);
        Error = folders.Items.Length > 0 ? "Нет связи с офисом. Можно продолжить работу с ранее скачанными файлами." :
            folders.Incomplete ? "Нет связи с офисом. Скачанные папки недоступны." :
                "Нет связи с офисом. На этом компьютере пока нет скачанных папок.";
        OnPropertyChanged(nameof(HasOfflineOfficeFolders));
        return folders;
    }

    internal Task<WorkflowOfflineFolders?> RefreshOfflineOfficeFoldersAsync(CancellationToken token) =>
        OfflineOfficeFolders is not null && _connection.RemoteClient is { } client
            ? ReadOfflineOfficeFoldersAsync(client, token) : Task.FromResult<WorkflowOfflineFolders?>(null);

    internal async Task<string?> VerifyOfflineOfficeFolderAsync(WorkflowOfflineFolder item, CancellationToken token)
    {
        if (OfflineOfficeFolders?.Items.Contains(item) != true || _connection.RemoteClient is not { } client) return null;
        try
        {
            var folder = await Task.Run(() => client.VerifyDownloadedFolder(item), token);
            return !_cuttingTaskDetached && !token.IsCancellationRequested && ReferenceEquals(client, _connection.RemoteClient) &&
                OfflineOfficeFolders?.Items.Contains(item) == true ? folder : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException or Jornal.Workflow.Core.WorkflowValidationException)
        { return null; }
    }
}
