using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private (WorkflowOfficeStorageClient Client, WorkflowOfflineFolders Folders)? _offlineOfficeFolders;
    [ObservableProperty] private WorkflowOfflineJournal? _offlineJournal;
    public bool ShowOfflineJournal => OfflineJournal is not null;
    public string OfflineJournalStatus => OfflineJournal?.Status ?? "";
    public bool ShowLiveOrderCard => !ShowOfflineJournal && IsCardVisible;
    public bool ShowLiveEmptyCard => !ShowOfflineJournal && IsNoCardSelected;
    public bool ShowOfflineOrderCard => ShowOfflineJournal && !IsCreating;

    internal bool SelectOfflineCalculation(WorkflowOfflineOrderRow row)
    {
        if (!CanUse || IsCreating || OfflineJournal is not { } journal ||
            journal.Selected != row || !row.HasDownloadedProject || OfflineOrderActor is not { } actor) return false;
        if (HasCurrentCardEdits && CurrentOrder?.Id != row.Id)
        {
            Error = "Сначала завершите редактирование текущей заявки.";
            return false;
        }
        if (CurrentOrder?.Id != row.Id)
        {
            CaptureCard();
            _workspace = journal.Workspace;
            RefreshEmployees(actor);
            SwitchCard(_workspace.Orders.Single(order => order.Id == row.Id));
        }
        return CanCalculateOrder;
    }

    private void NotifyOfflineCardVisibility()
    {
        OnPropertyChanged(nameof(ShowLiveOrderCard));
        OnPropertyChanged(nameof(ShowLiveEmptyCard));
        OnPropertyChanged(nameof(ShowOfflineOrderCard));
    }

    partial void OnOfflineJournalChanged(WorkflowOfflineJournal? value)
    {
        OnPropertyChanged(nameof(ShowOfflineJournal));
        OnPropertyChanged(nameof(OfflineJournalStatus));
        OnPropertyChanged(nameof(HasOfflineOfficeFolders));
        OnPropertyChanged(nameof(HasOfficePendingFilesNotice));
        NotifyOfflineCardVisibility();
        NotifyNewOrderActions();
    }

    private async Task RememberOfflineJournalAsync(WorkflowWorkspace workspace, CancellationToken token = default)
    {
        if (_connection.RemoteClient is not { } client) return;
        var savedAt = DateTimeOffset.Now;
        try { await Task.Run(() => client.RememberOfflineJournal(workspace, savedAt), token); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or WorkflowValidationException or OperationCanceledException)
        { /* A failed cache write leaves the live journal and the previous snapshot usable. */ }
    }

    private async Task ShowOfficeOutageAsync(WorkflowOfficeStorageClient client, CancellationToken token)
    {
        if (OfflineJournal is not null) return;
        var liveWasLoaded = _hasLoadedOrders;
        WorkflowOfflineJournal? journal;
        WorkflowOfflineFolders? folders;
        try
        {
            (journal, folders) = await Task.Run<(WorkflowOfflineJournal?, WorkflowOfflineFolders?)>(() =>
            {
                var snapshot = client.ReadOfflineJournal();
                if (snapshot is null && liveWasLoaded) return (null, (WorkflowOfflineFolders?)null);
                var files = client.ListDownloadedFolders(token);
                var offline = snapshot is null ? null : new WorkflowOfflineJournal(snapshot, files);
                if (offline is not null)
                {
                    try { foreach (var draft in client.ReadQueuedCalculations(token)) offline.ShowPendingCalculation(draft); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
                    { /* An unavailable draft folder leaves the saved orders readable. */ }
                }
                return (offline, files);
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        if (_cuttingTaskDetached || token.IsCancellationRequested || !ReferenceEquals(client, _connection.RemoteClient) || folders is null) return;
        _syncOfficeCalculations = true;
        OfflineJournal = journal;
        ApplyOfflineOfficeFolders(client, folders);
    }

    public bool HasOfflineOfficeFolders => OfflineOfficeFolders is { Items.Length: > 0 };

    private WorkflowOfflineFolders? OfflineOfficeFolders => (!_hasLoadedOrders || ShowOfflineJournal) && _offlineOfficeFolders is { } offline &&
        ReferenceEquals(offline.Client, _connection.RemoteClient) ? offline.Folders : null;

    private void ClearOfflineOfficeFolders()
    {
        OfflineJournal = null;
        if (_offlineOfficeFolders is null) return;
        _offlineOfficeFolders = null;
        OnPropertyChanged(nameof(HasOfflineOfficeFolders));
    }

    private async Task<WorkflowOfflineFolders?> ReadOfflineOfficeFoldersAsync(WorkflowOfficeStorageClient client, CancellationToken token)
    {
        var folders = await Task.Run(() => client.ListDownloadedFolders(token), token);
        if (_cuttingTaskDetached || token.IsCancellationRequested || _hasLoadedOrders && !ShowOfflineJournal || !ReferenceEquals(client, _connection.RemoteClient))
            return null;
        ApplyOfflineOfficeFolders(client, folders);
        return folders;
    }

    private void ApplyOfflineOfficeFolders(WorkflowOfficeStorageClient client, WorkflowOfflineFolders folders)
    {
        _offlineOfficeFolders = (client, folders);
        Error = ShowOfflineJournal ? null : folders.Items.Length > 0 ? "Нет связи с офисом. Можно продолжить работу с ранее скачанными файлами." :
            folders.Incomplete ? "Нет связи с офисом. Скачанные папки недоступны." :
                "Нет связи с офисом. На этом компьютере пока нет скачанных папок.";
        OnPropertyChanged(nameof(HasOfflineOfficeFolders));
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

    internal Task<string?> VerifyOfflineJournalProjectAsync(WorkflowOfflineProjectRow project, CancellationToken token) =>
        OfflineJournal?.Selected?.Projects.Contains(project) == true && project.Folder is { } folder
            ? VerifyOfflineOfficeFolderAsync(folder, token) : Task.FromResult<string?>(null);
}
