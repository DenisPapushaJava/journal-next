using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfflineFolder(Guid ProjectId, string Path, string ProjectName, string? OrderName)
{
    public bool HasOrderName => OrderName is not null;
}

internal sealed record WorkflowOfflineFolders(WorkflowOfflineFolder[] Items, bool Incomplete);

internal sealed partial class WorkflowOfficeStorageClient
{
    private const int MaximumOfflineJournal = 32 * 1024 * 1024;
    private string OfflineJournalPath => Path.Combine(profileDirectory, $".office-journal-{settings.DatabaseId:N}.json");
    private readonly object _offlineJournalWrite = new();
    private volatile bool _offlineJournalAccessRejected;

    internal void RememberOfflineJournal(WorkflowWorkspace workspace, DateTimeOffset savedAt, Guid? confirmedOrder = null)
    {
        lock (_offlineJournalWrite)
        {
            _offlineJournalAccessRejected = false;
            var saved = ReadOfflineJournal();
            if (saved is not null && saved.Workspace.Version > workspace.Version) return;
            if (confirmedOrder is { } id)
                workspace = MergeConfirmedOfficeOrder(saved?.Workspace ?? WorkflowWorkspace.Empty, workspace, id);
            var orders = workspace.Orders.Where(order => !order.IsDeleted).OrderByDescending(order => order.UpdatedAt)
                .ThenByDescending(order => order.Number).Take(100).ToArray();
            if (orders.Length == 0 && confirmedOrder is null && saved is not null) return;
            var snapshot = new WorkflowOfflineJournalSnapshot(1, settings.DatabaseId, settings.CredentialId, settings.Address,
                savedAt, workspace with { Orders = orders });
            var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
            if (bytes.Length > MaximumOfflineJournal) return;
            WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(OfflineJournalPath);
            Directory.CreateDirectory(profileDirectory);
            WriteLocalState(OfflineJournalPath, bytes);
        }
    }

    internal static WorkflowWorkspace MergeConfirmedOfficeOrder(WorkflowWorkspace before, WorkflowWorkspace fresh, Guid id)
    {
        if (before.Version > fresh.Version) return before;
        return fresh with { Orders = [.. before.Orders.Where(candidate => candidate.Id != id), .. fresh.Orders.Where(candidate => candidate.Id == id)] };
    }

    internal WorkflowOfflineJournalSnapshot? ReadOfflineJournal()
    {
        if (_offlineJournalAccessRejected) return null;
        try
        {
            WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(OfflineJournalPath);
            using var input = new FileStream(OfflineJournalPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length is <= 0 or > MaximumOfflineJournal) return null;
            var snapshot = JsonSerializer.Deserialize<WorkflowOfflineJournalSnapshot>(input, JsonOptions);
            if (snapshot is not { Format: 1, Workspace.Orders.Length: <= 100 } || snapshot.DatabaseId != settings.DatabaseId ||
                snapshot.CredentialId != settings.CredentialId || snapshot.Address != settings.Address || snapshot.SavedAt == default ||
                snapshot.Workspace.Orders.Any(order => order is null || order.IsDeleted)) return null;
            snapshot.Workspace.Validate();
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or WorkflowValidationException or
            ArgumentException or NotSupportedException or NullReferenceException)
        { return null; }
    }

    internal WorkflowOfflineFolders ListDownloadedFolders(CancellationToken token)
    {
        var items = new List<WorkflowOfflineFolder>();
        var incomplete = false;
        try
        {
            WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(CopyIndexFolder);
            var drives = DriveInfo.GetDrives();
            foreach (var id in FindLocalCopies(token))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (FindDownloadedFolder(id, drives) is { } folder) items.Add(DescribeOfflineFolder(id, folder));
                    else incomplete = true;
                }
                catch (Exception ex) when (IsCopyUnavailable(ex) || ex is ArgumentException or NotSupportedException)
                { incomplete = true; }
            }
        }
        catch (Exception ex) when (IsCopyUnavailable(ex)) { incomplete = true; }
        return new(items.OrderByDescending(item => item.OrderName, StringComparer.CurrentCulture)
            .ThenBy(item => item.ProjectName, StringComparer.CurrentCulture).ToArray(), incomplete);
    }

    internal string? VerifyDownloadedFolder(WorkflowOfflineFolder item)
    {
        if (FindDownloadedFolder(item.ProjectId, DriveInfo.GetDrives()) is not { } folder || !PathsEqual(folder, item.Path))
            return null;
        return folder;
    }

    internal string? DownloadedCalculationFolder(Guid project) => FindDownloadedFolder(project, DriveInfo.GetDrives());

    private string? FindDownloadedFolder(Guid project, DriveInfo[] drives)
    {
        var original = DefaultCopyFolder(project);
        var location = original + ".location";
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(location);
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ReadCopyLocation(location) ?? original));
        if (!IsLocalCopyPath(folder, drives)) return null;
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(Path.Combine(folder, SnapshotName));
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(Path.Combine(folder, CopyOwnerName));
        return Directory.Exists(folder) && OwnsCopy(folder, original) ? folder : null;
    }

    private static WorkflowOfflineFolder DescribeOfflineFolder(Guid id, string folder)
    {
        var projectName = Path.GetFileName(folder);
        var orderName = Path.GetFileName(Path.GetDirectoryName(folder));
        if (!Guid.TryParseExact(projectName, "N", out _))
            return new(id, folder, projectName, DirectoryDate(orderName) ? orderName : null);

        // Older flat copies use IDs for directory names. Display their actual CAM file name when it is unambiguous.
        var camFiles = Directory.EnumerateFiles(folder).Where(path =>
            Path.GetExtension(path).ToLowerInvariant() is ".rcam" or ".dsp" or ".dig" or ".aucb").Take(2).ToArray();
        return new(id, folder, camFiles.Length == 1 ? Path.GetFileNameWithoutExtension(camFiles[0]) : "Папка проекта", null);
    }

    private static bool DirectoryDate(string? name) => name is { Length: > 10 } &&
        DateOnly.TryParseExact(name[..10], "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);
}
