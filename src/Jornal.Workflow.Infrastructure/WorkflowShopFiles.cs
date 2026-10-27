using System.Security.Cryptography;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public enum ShopFileKind { Program, Map }
public sealed record ShopFile(string Name, string SourcePath, long Length, string Sha256, ShopFileKind Kind, bool Suggested, string Reason)
{
    public bool Ignored { get; init; }
    public bool IsReadable { get; init; } = true;
}
public sealed record ShopTreeFile(string RelativePath, long Length, string Sha256);
public sealed record ShopTree(ShopTreeFile[] Files, string[] Directories);
public sealed record ShopProjectFiles(CuttingProject Project, string Destination, ShopFile[] Files, ShopTree? Bending, string? Problem)
{
    public string? ReviewReason { get; init; }
    public string[]? CandidateExtensions { get; init; }
    public string[]? NativeNames { get; init; }
    public bool NeedsReview => ReviewReason is not null || Files.Any(f => !f.IsReadable || !f.Suggested && !f.Ignored);
}
public sealed record ShopTransferSelection(ShopProjectFiles Project, ShopFile[] Files, bool Reviewed, string? DestinationRevision);

// Explicit user operations only. No background scans, source markers or database writes.
internal static class ShopFileSystem
{
    internal static string[] Names(string folder, params string[] extensions) => Directory.EnumerateFiles(folder)
        .Where(p => extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).Select(Path.GetFileName).Cast<string>().Order(StringComparer.Ordinal).ToArray();
    internal static bool IsCandidate(string path, string[] extensions) => extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) &&
        !(Path.GetExtension(path).Equals(".doc", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal));
    internal static string[] CandidateNames(string folder, string[] extensions) => Directory.EnumerateFiles(folder).Where(p => IsCandidate(p, extensions))
        .Select(Path.GetFileName).Cast<string>().Order(StringComparer.Ordinal).ToArray();
    internal static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    internal static bool Under(string path, string root) => PathComparer.Equals(Path.GetFullPath(path), Path.GetFullPath(root)) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static void RequireOrdinaryPath(string path)
    {
        for (var part = Path.GetFullPath(path); !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
        {
            // GetAttributes also sees dangling links; Exists alone would hide them.
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new WorkflowValidationException($"Ссылки и перенаправленные папки пока не поддерживаются: {part}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static async Task<(long Length, string Hash)> FingerprintAsync(string path, CancellationToken token)
    {
        RequireOrdinaryPath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
        if (stream.Length != length) throw new WorkflowValidationException($"Файл изменился во время чтения: {Path.GetFileName(path)}. Повторите подбор.");
        return (length, hash);
    }

    internal static async Task<ShopTree> TreeAsync(string root, CancellationToken token, string? ignoreName = null)
    {
        RequireOrdinaryPath(root);
        if (!Directory.Exists(root)) throw new WorkflowValidationException($"Папка недоступна: {root}");
        var files = new List<ShopTreeFile>(); var directories = new List<string>();
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var folder))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested(); RequireOrdinaryPath(path);
                var relative = Path.GetRelativePath(root, path);
                if (relative == ignoreName) continue;
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0) { directories.Add(relative); pending.Push(path); }
                else { var f = await FingerprintAsync(path, token); files.Add(new(relative, f.Length, f.Hash)); }
            }
        }
        if (files.Select(f => f.RelativePath).Concat(directories).Distinct(PathComparer).Count() != files.Count + directories.Count)
            throw new WorkflowValidationException("В папке совпадают имена без учёта регистра. Проверьте комплект перед переносом на Windows.");
        return new(files.ToArray(), directories.ToArray());
    }

    internal static bool SameTree(ShopTree a, ShopTree b) =>
        a.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal).SequenceEqual(b.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal)) &&
        a.Directories.Order(StringComparer.Ordinal).SequenceEqual(b.Directories.Order(StringComparer.Ordinal));
}
