using System.Security.Cryptography;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfficeCleanupCandidate(Guid OrderId, CuttingProject Project, string Folder, long Bytes);
internal sealed record WorkflowOfficeCleanupPlan(WorkflowOfficeCleanupCandidate[] Copies, string[] Skipped);
internal sealed record WorkflowOfficeCleanupResult(int Removed, long Bytes, string[] Skipped, bool Stopped, string? StopReason = null);

internal sealed partial class WorkflowOfficeStorageClient
{
    private sealed record CopyContents(string[] Files, string[] Directories, Dictionary<string, string> Versions, long Bytes);
    private const string RecoveryFolder = ".office-sync";

    internal async Task<WorkflowOfficeCleanupPlan> PreviewCleanupAsync(WorkflowOrder[] orders, IReadOnlySet<Guid> protectedProjects,
        string? projectRoot, string? shopRoot, CancellationToken token)
    {
        var ids = FindLocalCopies(token);
        var missing = ids.Where(id => !protectedProjects.Contains(id)).ToHashSet();
        var projects = new Dictionary<Guid, (WorkflowOrder Order, CuttingProject Project)>();
        await FindOfficeProjectsAsync(missing, orders, projects, token);
        var names = orders.SelectMany(order => order.Projects).ToLookup(project => project.Id, project => project.Name);
        var copies = new List<WorkflowOfficeCleanupCandidate>();
        var skipped = new List<string>();
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            if (!projects.TryGetValue(id, out var target))
            {
                var name = names[id].FirstOrDefault() ?? id.ToString("D");
                skipped.Add(protectedProjects.Contains(id) ? $"{name} — есть черновик расчёта." : $"{name} — проект недоступен в офисе.");
                continue;
            }
            try
            {
                if (ReadCalculationDraft(target.Order.Id)?.Entries.Any(entry => entry.ProjectId == id) == true)
                    throw new WorkflowValidationException("Есть незавершённый расчёт.");
                await _files.WaitAsync(token);
                try
                {
                    var original = DefaultCopyFolder(id);
                    using var lease = LockCleanupCopy(original);
                    var folder = RequireCleanupFolder(original, target.Project, projectRoot, shopRoot);
                    var versions = await ReadBaselineAsync(folder, token);
                    var contents = await InspectCopyAsync(folder, versions, token);
                    VerifyRecoverable(await ManifestAsync(ProjectRoute(target.Order.Id, id), token, workingFiles: true), contents);
                    copies.Add(new(target.Order.Id, target.Project, folder, contents.Bytes));
                }
                finally { _files.Release(); }
            }
            catch (Exception ex) when (IsCleanupUnavailable(ex))
            { skipped.Add(CleanupProblem(target.Project.Name, ex)); }
        }
        return new(copies.ToArray(), skipped.ToArray());
    }

    internal async Task<WorkflowOfficeCleanupResult> RemoveCleanCopiesAsync(WorkflowOfficeCleanupPlan plan,
        IReadOnlySet<Guid> protectedProjects, string? projectRoot, string? shopRoot, CancellationToken token)
    {
        var removed = 0;
        long bytes = 0;
        var skipped = new List<string>();
        var orders = new Dictionary<Guid, WorkflowOrder>();
        try
        {
            foreach (var copy in plan.Copies)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (protectedProjects.Contains(copy.Project.Id))
                        throw new WorkflowValidationException("Есть черновик расчёта.");
                    if (ReadCalculationDraft(copy.OrderId)?.Entries.Any(entry => entry.ProjectId == copy.Project.Id) == true)
                        throw new WorkflowValidationException("Есть незавершённый расчёт.");
                    if (!orders.TryGetValue(copy.OrderId, out var order))
                    {
                        order = await ReadActiveOrderForWorkingFilesAsync(copy.OrderId, token);
                        orders.Add(copy.OrderId, order);
                    }
                    var project = order.ActiveProjects.SingleOrDefault(project => project.Id == copy.Project.Id)
                        ?? throw new WorkflowValidationException("Проект недоступен в офисе.");
                    await _files.WaitAsync(token);
                    try
                    {
                        var original = DefaultCopyFolder(copy.Project.Id);
                        using var lease = LockCleanupCopy(original);
                        var folder = RequireCleanupFolder(original, project, projectRoot, shopRoot);
                        if (!PathsEqual(folder, copy.Folder))
                            throw new WorkflowValidationException("Расположение копии изменилось. Проверьте её заново.");
                        bytes += await RemoveCopyAsync(copy, original, folder, token);
                        removed++;
                    }
                    finally { _files.Release(); }
                }
                catch (Exception ex) when (IsCleanupUnavailable(ex))
                { skipped.Add(CleanupProblem(copy.Project.Name, ex)); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(removed, bytes, skipped.ToArray(), true); }
        catch (WorkflowOfficeSyncUnavailableException ex)
        { return new(removed, bytes, skipped.ToArray(), true, ex.Message); }
        return new(removed, bytes, skipped.ToArray(), false);
    }

    private static FileStream LockCleanupCopy(string original)
    {
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(original + ".lock");
        return LockCopy(original);
    }

    private string RequireCleanupFolder(string original, CuttingProject project, string? projectRoot, string? shopRoot)
    {
        RecoverInterruptedCleanup(original);
        var location = original + ".location";
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(location);
        var folder = ReadCopyLocation(location) ?? original;
        var nativeFolder = Path.IsPathFullyQualified(project.NativePath ?? "") ? Path.GetDirectoryName(project.NativePath) : null;
        var drives = DriveInfo.GetDrives();
        if (!IsLocalCopyPath(folder, drives) || drives.Any(candidate => Under(candidate.RootDirectory.FullName, folder)) ||
            new[] { projectRoot, shopRoot, nativeFolder }.Any(root => root is not null && Path.IsPathFullyQualified(root) &&
                (Under(folder, root) || Under(root, folder))))
            throw new WorkflowValidationException("Очистка доступна для отдельной локальной папки копий.");
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(folder);
        if (!Directory.Exists(folder)) throw new WorkflowValidationException("Локальная копия недоступна.");
        RequireCleanupOwner(folder, original);
        return folder;
    }

    private static bool IsLocalCopyPath(string folder, DriveInfo[] drives)
    {
        if (OperatingSystem.IsWindows() && folder.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var drive = drives.Where(candidate => Under(folder, candidate.RootDirectory.FullName))
            .MaxBy(candidate => candidate.Name.Length);
        return drive is not null && drive.DriveType is not (DriveType.Network or DriveType.NoRootDirectory or DriveType.Unknown);
    }

    private void RequireCleanupOwner(string folder, string original)
    {
        var owner = Path.Combine(folder, CopyOwnerName);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(owner);
        if (!File.Exists(owner) || File.ReadAllText(owner) != CopyOwner(original))
            throw new WorkflowValidationException("Не удалось подтвердить принадлежность папки к локальным копиям.");
        var snapshot = Path.Combine(folder, SnapshotName);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(snapshot);
        if (!File.Exists(snapshot))
            throw new WorkflowValidationException("Не найден список исходных версий локальной копии.");
    }

    private async Task<long> RemoveCopyAsync(WorkflowOfficeCleanupCandidate copy, string original, string folder, CancellationToken token)
    {
        var office = await ManifestAsync(ProjectRoute(copy.OrderId, copy.Project.Id), token, workingFiles: true);
        var move = new CleanupMove(folder, Guid.NewGuid(), InContainer: true);
        var detached = DetachedCleanupFolder(move);
        token.ThrowIfCancellationRequested();
        SaveCleanupMove(original, move);
        long bytes;
        try
        {
            token.ThrowIfCancellationRequested();
            var container = CleanupContainer(move);
            WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(container);
            Directory.CreateDirectory(container);
            WorkflowServiceFiles.TryHide(container);
            Directory.Move(folder, detached);
            RequireCleanupOwner(detached, original);
            var baseline = await ReadBaselineAsync(detached, token);
            var held = new List<FileStream>();
            CopyContents contents;
            try
            {
                contents = await InspectCopyAsync(detached, baseline, token, held);
                VerifyRecoverable(office, contents);
                token.ThrowIfCancellationRequested();

                // Keep the Windows share restrictions in force until the checked files are marked for deletion.
                foreach (var file in contents.Files)
                {
                    var path = Path.Combine(detached, file);
                    WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
                    File.Delete(path);
                }
            }
            finally { foreach (var stream in held) stream.Dispose(); }

            foreach (var directory in contents.Directories.OrderByDescending(path => path.Length))
            {
                var path = Path.Combine(detached, directory);
                WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
                Directory.Delete(path);
            }
            RequireCleanupOwner(detached, original);
            SaveCleanupMove(original, move with { WorkingFilesRemoved = true });
            File.Delete(Path.Combine(detached, CopyOwnerName));
            File.Delete(Path.Combine(detached, SnapshotName));
            Directory.Delete(detached);
            bytes = contents.Bytes;
        }
        catch
        {
            RecoverInterruptedCleanup(original);
            throw;
        }
        RecoverInterruptedCleanup(original);
        return bytes;
    }

    private static async Task<CopyContents> InspectCopyAsync(string folder, Dictionary<string, string> baseline,
        CancellationToken token, List<FileStream>? held = null)
    {
        var files = new List<string>();
        var directories = new List<string>();
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        foreach (var (path, relative, directory) in WorkflowOfficeProjectPaths.CopyEntries(folder, token))
        {
            var name = relative.Replace(Path.DirectorySeparatorChar, '/');
            if (!names.Add(name))
                throw new WorkflowValidationException("В копии совпадают имена файлов или папок. Проверьте комплект.");
            if (directory)
            {
                directories.Add(relative);
                if (name != RecoveryFolder && name.Split('/').Any(part => part.StartsWith(".", StringComparison.Ordinal)))
                    throw new WorkflowValidationException("В копии есть черновики или сохранённые версии.");
                continue;
            }
            if (name is CopyOwnerName or SnapshotName) continue;
            if (name.StartsWith(RecoveryFolder + "/", StringComparison.OrdinalIgnoreCase))
                throw new WorkflowValidationException("В копии есть сохранённые версии для восстановления.");
            if (!baseline.TryGetValue(name, out var previous) || WorkflowOfficeProjectPaths.FileInProject(folder, name) is null)
                throw new WorkflowValidationException("В копии есть файлы, которых нет в последней синхронизации.");
            var input = new FileStream(path, FileMode.Open, FileAccess.Read, held is null ? FileShare.None : FileShare.Delete,
                held is null ? 65536 : 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            held?.Add(input);
            try
            {
                var length = input.Length;
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
                if (hash != previous || input.Length != length || !versions.TryAdd(name, hash))
                    throw new WorkflowValidationException("В копии есть изменения. Проверьте и отправьте файлы в офис.");
                files.Add(relative);
                bytes = checked(bytes + length);
            }
            finally { if (held is null) await input.DisposeAsync(); }
        }
        if (versions.Count != baseline.Count)
            throw new WorkflowValidationException("В локальной папке удалены файлы. Откройте проект для сверки с офисом.");
        return new(files.ToArray(), directories.ToArray(), versions, bytes);
    }

    private static void VerifyRecoverable(ProjectManifest office, CopyContents copy)
    {
        var files = office.Files.ToDictionary(file => file.Path, file => file.Sha256, StringComparer.OrdinalIgnoreCase);
        var folders = office.Directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (copy.Versions.Any(file => files.GetValueOrDefault(file.Key) != file.Value) ||
            copy.Directories.Any(path => path != RecoveryFolder && !folders.Contains(path.Replace(Path.DirectorySeparatorChar, '/'))))
            throw new WorkflowValidationException("Офисная копия отличается. Откройте папку проекта, чтобы обновить файлы.");
    }

    private static bool IsCleanupUnavailable(Exception error) =>
        error is not WorkflowOfficeSyncUnavailableException &&
        (IsCopyUnavailable(error) || error is WorkflowConflictException or ArgumentException or NotSupportedException);

    private static bool PathsEqual(string path, string other) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool Under(string path, string root)
    {
        var absolute = Path.GetFullPath(path);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(Path.TrimEndingDirectorySeparator(absolute), parent, StringComparison.OrdinalIgnoreCase) ||
            absolute.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanupProblem(string name, Exception error) =>
        $"{name} — " + (error switch
        {
            WorkflowValidationException => error.Message,
            _ => "Не удалось проверить папку. Закройте открытые файлы и проверьте доступ."
        });
}
