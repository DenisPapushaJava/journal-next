using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public static class WorkflowOfficeProjectPaths
{
    public const string WorkingFileUploadHeader = "X-Journal-Working-File";

    public static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    public static string? AcceptedReportName(WorkflowOrder order, Guid projectId)
    {
        var state = order.Specifications.SingleOrDefault(specification => specification.Id == projectId)?.Current;
        return state?.SourcePath is { } path ? FileName(path) : state?.Report?.FileName;
    }

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

    public static IEnumerable<(string Path, string Relative, bool Directory)> CopyEntries(string folder, CancellationToken token) =>
        ShopFileSystem.TreeEntries(folder, token, ignoreName: null);

    public static void RequireOrdinaryCopyPath(string path) => ShopFileSystem.RequireOrdinaryPath(path);

    public static Predicate<string> ShopSourceFilter(CuttingProject project, WorkflowMachine machine, SpecificationState calculation,
        bool includeAcceptedReport = true)
    {
        var native = Path.GetExtension(FileName(project.NativePath!));
        var report = includeAcceptedReport && calculation.SourcePath is { } path ? FileName(path) : null;
        var extensions = WorkflowShopDiscovery.TransferExtensions(machine);
        return relative => relative.Contains('/') ?
            calculation.Parameters.HasBending && relative.StartsWith(WorkflowBendingFiles.FolderName + "/", StringComparison.OrdinalIgnoreCase) :
            string.Equals(report, relative, StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(relative).Equals(native, StringComparison.OrdinalIgnoreCase) ||
            ShopFileSystem.IsCandidate(relative, extensions);
    }

    public static async Task<(ShopTree Tree, string[] UnavailableFiles)> ShopManifestAsync(string folder, CuttingProject project, WorkflowMachine machine,
        SpecificationState calculation, CancellationToken token)
    {
        ShopFileSystem.RequireOrdinaryPath(folder);
        var files = new List<ShopTreeFile>();
        var unavailable = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var includes = ShopSourceFilter(project, machine, calculation);
        var nativeExtension = Path.GetExtension(FileName(project.NativePath!));
        foreach (var path in ShopFileSystem.SourceFiles(folder))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (!IsProjectEntry(name) || !includes(name)) continue;
            if (names.Count == 2000 || !names.Add(name))
                throw new WorkflowValidationException("Слишком много файлов или совпадают имена без учёта регистра. Проверьте рабочую папку.");
            try
            {
                var (length, hash) = await FingerprintAsync(path, token);
                files.Add(new(name, length, hash));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Path.GetExtension(name).Equals(nativeExtension, StringComparison.OrdinalIgnoreCase) ||
                    calculation.SourcePath is { } report && FileName(report).Equals(name, StringComparison.OrdinalIgnoreCase))
                    throw new WorkflowValidationException($"Файл «{name}» недоступен в офисной папке. Закройте его и повторите подбор.");
                unavailable.Add(name);
            }
        }
        if (!calculation.Parameters.HasBending) return (new(files.ToArray(), []), unavailable.ToArray());
        var bending = WorkflowBendingFiles.FolderName;
        var tree = await ManifestAsync(Path.Combine(folder, bending), token);
        string UnderBending(string path) => bending + "/" + path.Replace(Path.DirectorySeparatorChar, '/');
        return (new([.. files, .. tree.Files.Select(file => file with { RelativePath = UnderBending(file.RelativePath) })],
            [bending, .. tree.Directories.Select(UnderBending)]), unavailable.ToArray());
    }

    public static IEnumerable<string> LocalWorkingFiles(string root, CancellationToken token, bool requireUniqueNames = false)
    {
        ShopFileSystem.RequireOrdinaryPath(root);
        var folders = new Stack<string>();
        folders.Push(root);
        while (folders.TryPop(out var folder))
        {
            var names = requireUniqueNames ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (!IsProjectEntry(name)) continue;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (names?.Add(name) == false)
                    throw new WorkflowValidationException("В локальной папке совпадают имена без учёта регистра. Проверьте комплект перед переносом на Windows.");
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
