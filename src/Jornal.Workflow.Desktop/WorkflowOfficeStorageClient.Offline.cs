using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfflineFolder(Guid ProjectId, string Path, string ProjectName, string? OrderName)
{
    public bool HasOrderName => OrderName is not null;
}

internal sealed record WorkflowOfflineFolders(WorkflowOfflineFolder[] Items, bool Incomplete);

internal sealed partial class WorkflowOfficeStorageClient
{
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
