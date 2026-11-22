using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public static class WorkflowOfficeProjectPaths
{
    public static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    public static string? InRoot(string? root, string nativePath)
    {
        if (root is null || !Path.IsPathFullyQualified(nativePath)) return null;
        var folder = Path.GetDirectoryName(Path.GetFullPath(nativePath));
        if (folder is null) return null;
        var relative = Path.GetRelativePath(root, folder);
        return Path.IsPathRooted(relative) || relative is "." or ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? null : folder;
    }

    public static string? FileInProject(string folder, string relative)
    {
        if (relative.Split('/').Any(part => !IsProjectEntry(part))) return null;
        var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        ShopFileSystem.RequireOrdinaryPath(path);
        return path;
    }

    public static Task<ShopTree> ManifestAsync(string folder, CancellationToken token) =>
        ShopFileSystem.TreeAsync(folder, token, includeEntry: IsProjectEntry);

    public static IEnumerable<string> LocalWorkingFiles(string root, CancellationToken token)
    {
        ShopFileSystem.RequireOrdinaryPath(root);
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.TryPop(out var folder))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                if (!IsProjectEntry(Path.GetFileName(path))) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0) folders.Push(path);
                else yield return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            }
        }
    }

    public static async Task<(ShopTreeFile[] Files, string[] UnavailableFiles)> ReportManifestAsync(
        string folder, string profile, string? reportName, CancellationToken token)
    {
        ShopFileSystem.RequireOrdinaryPath(folder);
        var files = new List<ShopTreeFile>(); var unavailable = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!IsProjectEntry(name) || !WorkflowReportFiles.IsReport(name, profile) ||
                reportName is not null && !names.Comparer.Equals(reportName, name)) continue;
            if (!names.Add(name)) throw new WorkflowValidationException("В папке есть спецификации с одинаковыми именами без учёта регистра.");
            try
            {
                var (length, hash) = await FingerprintAsync(path, token);
                files.Add(new(name, length, hash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
            { unavailable.Add(name); }
        }
        return (files.ToArray(), unavailable.ToArray());
    }

    public static Task<(long Length, string Hash)> FingerprintAsync(string path, CancellationToken token) =>
        ShopFileSystem.FingerprintAsync(path, token);

    public static string CreateStateFolder(string folder)
    {
        var state = Path.Combine(folder, ".office-sync");
        ShopFileSystem.RequireOrdinaryPath(state);
        Directory.CreateDirectory(state);
        WorkflowServiceFiles.TryHide(state);
        return state;
    }

    private static bool IsProjectEntry(string name)
    {
        if (name.Length == 0 || name.EndsWith('.') || name.EndsWith(' ') || name.Any(char.IsControl) ||
            name.AsSpan().IndexOfAny("/\\:<>\"|?*") >= 0) return false;
        var stem = name.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            "123456789¹²³".Contains(stem[^1])) return false;
        return !name.StartsWith(".workflow-", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith(".journal-next-", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith(".office-", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("~$", StringComparison.Ordinal) &&
            !name.Equals(WorkflowOfficePilotPath.FileName, StringComparison.OrdinalIgnoreCase);
    }
}
