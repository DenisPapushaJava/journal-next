using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfficeFileConflict(string Path, string HomeHash, string? OfficeHash);
internal sealed record WorkflowOfficeWorkingCopy(string Folder, IReadOnlyList<WorkflowOfficeFileConflict> Conflicts, bool? HasPendingFiles)
{
    internal bool IsOffline { get; init; }
}
internal sealed record WorkflowOfficeFileComparison(string Folder, string? OfficeFile, string HomeHash, string? OfficeHash);
internal sealed record WorkflowOfficeFilePublication(int Sent, string[] Problems, string? StopReason = null);
internal class WorkflowOfficeSyncUnavailableException(string message, Exception? inner = null) : IOException(message, inner);
internal sealed class WorkflowOfficeConnectionLostException(string message, Exception? inner = null) : WorkflowOfficeSyncUnavailableException(message, inner);

internal sealed partial class WorkflowOfficeStorageClient
{
    private const string SnapshotName = ".office-copy.json";
    private const string CopyOwnerName = ".office-copy-owner";
    private static readonly HttpClient FileTransport = WorkflowOfficeTransport.CreateClient(TimeSpan.FromMinutes(5));
    private readonly SemaphoreSlim _files = new(1, 1);
    private sealed record ProjectFile(string Path, long Length, string Sha256);
    private sealed record ProjectManifest(string[] Directories, ProjectFile[] Files, string[]? UnavailableFiles = null);

    internal async Task<string> OpenCopyAsync(Guid order, Guid project, CancellationToken cancellation, string? copiesRoot = null, string? nativePath = null)
    {
        var copy = await OpenWorkingCopyAsync(order, project, cancellation, copiesRoot, nativePath);
        if (copy.Conflicts.FirstOrDefault() is { } conflict)
            throw new WorkflowValidationException($"Откройте папку проекта через список проектов и выберите версию файла «{conflict.Path}».");
        return copy.Folder;
    }

    internal async Task<WorkflowOfficeWorkingCopy> OpenWorkingCopyAsync(Guid order, Guid project, CancellationToken cancellation,
        string? copiesRoot = null, string? nativePath = null, bool allowOffline = false)
    {
        await _files.WaitAsync(cancellation);
        try
        {
            var originalFolder = DefaultCopyFolder(project);
            using var lease = LockCopy(originalFolder);
            RecoverInterruptedCleanup(originalFolder);
            var (folder, location, hasCopy) = ResolveCopyFolder(originalFolder, copiesRoot, nativePath);
            var route = ProjectRoute(order, project);
            if (hasCopy)
            {
                RecordCopyLocation(location, folder);
                HideCopyMarkers(folder);
                try { return await RefreshWorkingCopyAsync(folder, route, cancellation, allowOffline); }
                catch (WorkflowOfficeConnectionLostException) when (allowOffline)
                {
                    cancellation.ThrowIfCancellationRequested();
                    WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(folder);
                    if (!OwnsCopy(folder, originalFolder)) throw;
                    return new(folder, [], null) { IsOffline = true };
                }
            }
            CheckNewCopyFolder(folder, copiesRoot);
            var manifest = await ManifestAsync(route, cancellation);
            await CreateCopyAsync(originalFolder, folder, route, manifest, cancellation);
            RecordCopyLocation(location, folder);
            return new(folder, [], false);
        }
        finally { _files.Release(); }
    }

    private static void CheckNewCopyFolder(string folder, string? copiesRoot)
    {
        if (Directory.Exists(folder) || File.Exists(folder))
            throw new WorkflowValidationException("Локальная папка проекта уже существует. Проверьте её содержимое.");
        if (copiesRoot is not null && !Directory.Exists(copiesRoot))
            throw new WorkflowValidationException("Папка для локальных копий недоступна. Проверьте её в настройках хранилища.");
    }

    private async Task CreateCopyAsync(string original, string folder, string route, ProjectManifest manifest, CancellationToken token)
    {
        var parent = Path.GetDirectoryName(folder)!;
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".office-" + Guid.NewGuid().ToString("N") + ".tmp");
        var stage = Path.Combine(staging, "copy");
        Directory.CreateDirectory(stage);
        WorkflowServiceFiles.TryHide(staging);
        try
        {
            foreach (var directory in manifest.Directories)
                Directory.CreateDirectory(Child(stage, directory));
            foreach (var file in manifest.Files)
            {
                var destination = Child(stage, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await DownloadFileAsync(route, file, destination, token);
            }
            await SaveBaselineAsync(stage, manifest.Files.ToDictionary(file => file.Path, file => file.Sha256,
                StringComparer.OrdinalIgnoreCase), token);
            var owner = Path.Combine(stage, CopyOwnerName);
            await File.WriteAllTextAsync(owner, CopyOwner(original), token);
            WorkflowServiceFiles.TryHide(owner);
            Directory.Move(stage, folder);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* A remaining staging folder is hidden. */ }
        }
    }

    private async Task<WorkflowOfficeWorkingCopy> RefreshWorkingCopyAsync(string folder, string route, CancellationToken token, bool workingFiles)
    {
        var baseline = await ReadBaselineAsync(folder, token);
        var manifest = await ManifestAsync(route, token, workingFiles: workingFiles);
        var current = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var local = IndexWorkingFiles(folder, token);
        var conflicts = new List<WorkflowOfficeFileConflict>();
        string? staging = null;
        foreach (var directory in manifest.Directories)
            Directory.CreateDirectory(Child(folder, directory));
        foreach (var file in manifest.Files)
        {
            var previous = baseline.GetValueOrDefault(file.Path);
            var homeName = local.GetValueOrDefault(file.Path) ?? file.Path;
            var destination = Child(folder, homeName);
            var exists = local.ContainsKey(file.Path);
            // An unchanged office file cannot overwrite or conflict with a home edit.
            if (exists && file.Sha256 == previous) continue;
            var home = exists ? (await WorkflowOfficeProjectPaths.FingerprintAsync(destination, token)).Hash : null;
            if (home is not null && home != file.Sha256 && home != previous)
            {
                conflicts.Add(new(homeName, home, file.Sha256));
                continue;
            }
            if (home != file.Sha256)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                staging ??= WorkflowOfficeProjectPaths.CreateStateFolder(folder);
                var stage = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    await DownloadFileAsync(route, file, stage, token, workingFiles);
                    if (home is null) File.Move(stage, destination, overwrite: false);
                    else if ((await WorkflowOfficeProjectPaths.FingerprintAsync(destination, token)).Hash == home)
                        await ReplaceLocalFileAsync(stage, destination, home);
                    else throw LocalFileChanged(homeName);
                }
                finally { File.Delete(stage); }
            }
            baseline[homeName] = file.Sha256;
            await SaveBaselineAsync(folder, baseline, token);
        }
        foreach (var file in baseline.Keys.Where(path => !current.ContainsKey(path)).ToArray())
        {
            if (local.TryGetValue(file, out var homeName))
            {
                var path = Child(folder, homeName);
                var home = (await WorkflowOfficeProjectPaths.FingerprintAsync(path, token)).Hash;
                if (home != baseline[file])
                {
                    conflicts.Add(new(homeName, home, null));
                    continue;
                }
                await MoveLocalFileToRecoveryAsync(folder, path, home);
            }
            baseline.Remove(file);
            await SaveBaselineAsync(folder, baseline, token);
        }
        return new(folder, conflicts, conflicts.Count > 0 ? true : await CheckPendingFilesAsync(folder, baseline, token));
    }

    private static Dictionary<string, string> IndexWorkingFiles(string folder, CancellationToken token, Predicate<string>? includes = null)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in WorkflowOfficeProjectPaths.LocalWorkingFiles(folder, token))
            if ((includes is null || includes(path)) && !files.TryAdd(path, path))
                throw new WorkflowValidationException("В папке есть файлы с одинаковыми именами без учёта регистра.");
        return files;
    }

    internal async Task<bool?> HasPendingFilesAsync(Guid project, string? copiesRoot, string? nativePath, CancellationToken token,
        bool indexedCopy = false)
    {
        await _files.WaitAsync(token);
        try
        {
            var original = DefaultCopyFolder(project);
            var (folder, _, hasCopy) = ResolveCopyFolder(original, copiesRoot, nativePath);
            if (!hasCopy) return indexedCopy ? null : false;
            using var lease = LockCopy(original);
            return await CheckPendingFilesAsync(folder, await ReadBaselineAsync(folder, token), token);
        }
        finally { _files.Release(); }
    }

    private static async Task<bool?> CheckPendingFilesAsync(string folder, Dictionary<string, string> baseline, CancellationToken token)
    {
        var incomplete = false;
        try
        {
            foreach (var file in WorkflowOfficeProjectPaths.LocalWorkingFiles(folder, token))
            {
                try
                {
                    var hash = (await WorkflowOfficeProjectPaths.FingerprintAsync(Child(folder, file), token)).Hash;
                    if (baseline.GetValueOrDefault(file) != hash) return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
                { incomplete = true; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
        { incomplete = true; }
        return incomplete ? null : false;
    }

    private static WorkflowValidationException LocalFileChanged(string path) =>
        new($"Файл «{path}» изменился во время сверки. Завершите сохранение и повторите открытие папки.");

    private static async Task MoveLocalFileToRecoveryAsync(string folder, string path, string expectedHash)
    {
        var recovery = Directory.CreateDirectory(Path.Combine(WorkflowOfficeProjectPaths.CreateStateFolder(folder), Guid.NewGuid().ToString("N")));
        var saved = Path.Combine(recovery.FullName, Path.GetFileName(path));
        File.Move(path, saved, overwrite: false);
        if ((await WorkflowOfficeProjectPaths.FingerprintAsync(saved, CancellationToken.None)).Hash != expectedHash)
            throw new WorkflowValidationException($"Файл изменился во время обновления. Его последняя версия сохранена в «{saved}».");
    }

    internal async Task<WorkflowOfficeFileComparison> CompareWorkingFileAsync(Guid order, Guid project,
        WorkflowOfficeFileConflict conflict, CancellationToken token, bool refreshVersions = false)
    {
        await _files.WaitAsync(token);
        try
        {
            var original = DefaultCopyFolder(project);
            using var lease = LockCopy(original);
            var folder = RequireWorkingCopy(original);
            var route = ProjectRoute(order, project);
            var office = await ReadConflictingOfficeFileAsync(route, conflict, token, refreshVersions);
            var comparison = await CaptureComparedFilesAsync(folder, Child(folder, conflict.Path), route, office, token);
            if (!refreshVersions && comparison.HomeHash != conflict.HomeHash)
            {
                Directory.Delete(comparison.Folder, recursive: true);
                throw LocalFileChanged(conflict.Path);
            }
            return comparison;
        }
        finally { _files.Release(); }
    }

    internal async Task ChooseWorkingFileAsync(Guid order, Guid project, WorkflowOfficeFileConflict conflict,
        WorkflowOfficeFileComparison comparison, bool useOffice, CancellationToken token)
    {
        await _files.WaitAsync(token);
        try
        {
            var original = DefaultCopyFolder(project);
            using var lease = LockCopy(original);
            var folder = RequireWorkingCopy(original);
            var home = Child(folder, conflict.Path);
            var office = await ReadConflictingOfficeFileAsync(ProjectRoute(order, project), conflict, token);
            if (!File.Exists(home) || (await WorkflowOfficeProjectPaths.FingerprintAsync(home, token)).Hash != conflict.HomeHash)
                throw LocalFileChanged(conflict.Path);

            var baseline = await ReadBaselineAsync(folder, token);
            if (useOffice)
            {
                if (office is null) await MoveLocalFileToRecoveryAsync(folder, home, conflict.HomeHash);
                else await InstallComparedFileAsync(folder, home, comparison.OfficeFile!, office.Sha256, conflict.HomeHash, token);
            }
            // The choice acknowledges only the compared version. Publishing still checks the server before writing.
            if (office is null) baseline.Remove(conflict.Path);
            else baseline[conflict.Path] = office.Sha256;
            await SaveBaselineAsync(folder, baseline, token);
        }
        finally { _files.Release(); }
    }

    private async Task<ProjectFile?> ReadConflictingOfficeFileAsync(string route, WorkflowOfficeFileConflict conflict, CancellationToken token,
        bool refreshVersions = false)
    {
        var manifest = await ManifestAsync(route, token);
        var office = manifest.Files.SingleOrDefault(file => file.Path.Equals(conflict.Path, StringComparison.OrdinalIgnoreCase));
        if (!refreshVersions && office?.Sha256 != conflict.OfficeHash)
            throw new WorkflowValidationException($"Офисный файл «{conflict.Path}» снова изменился. Начните сравнение заново.");
        return office;
    }

    private string RequireWorkingCopy(string original)
    {
        var (folder, _, hasCopy) = ResolveCopyFolder(original, null, null);
        return hasCopy ? folder : throw new WorkflowValidationException("Локальная копия проекта недоступна.");
    }

    private async Task<WorkflowOfficeFileComparison> CaptureComparedFilesAsync(string folder, string home,
        string route, ProjectFile? office, CancellationToken token)
    {
        var comparison = Path.Combine(WorkflowOfficeProjectPaths.CreateStateFolder(folder), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(comparison);
        try
        {
            var extension = Path.GetExtension(home);
            var homeCopy = Path.Combine(comparison, "Локальная" + extension);
            await using (var input = File.Open(home, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var output = File.Open(homeCopy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await input.CopyToAsync(output, token);
            var homeHash = (await WorkflowOfficeProjectPaths.FingerprintAsync(homeCopy, token)).Hash;
            var officeCopy = office is null ? null : Path.Combine(comparison, "Офисная" + extension);
            if (office is not null) await DownloadFileAsync(route, office, officeCopy!, token);
            return new(comparison, officeCopy, homeHash, office?.Sha256);
        }
        catch { Directory.Delete(comparison, recursive: true); throw; }
    }

    private static async Task InstallComparedFileAsync(string folder, string home, string officeCopy, string officeHash,
        string homeHash, CancellationToken token)
    {
        var stage = Path.Combine(WorkflowOfficeProjectPaths.CreateStateFolder(folder), Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(officeCopy, stage, overwrite: false);
            if ((await WorkflowOfficeProjectPaths.FingerprintAsync(stage, token)).Hash != officeHash)
                throw new WorkflowValidationException("Копия для сравнения изменена. Откройте сравнение ещё раз.");
            token.ThrowIfCancellationRequested();
            await ReplaceLocalFileAsync(stage, home, homeHash);
        }
        finally { File.Delete(stage); }
    }

    internal async Task<WorkflowOfficeFilePublication> PublishCopyAsync(Guid order, Guid project, CancellationToken token,
        string? copiesRoot = null, string? nativePath = null, string? reportName = null, CuttingProject? verifyProject = null)
    {
        await _files.WaitAsync(token);
        try
        {
            var originalFolder = DefaultCopyFolder(project);
            using var lease = LockCopy(originalFolder);
            var (folder, location, hasCopy) = ResolveCopyFolder(originalFolder, copiesRoot, nativePath);
            if (!hasCopy)
                throw new WorkflowValidationException("Сначала откройте папку проекта, чтобы скачать рабочие файлы.");
            var baseline = await ReadBaselineAsync(folder, token);
            RecordCopyLocation(location, folder);
            var route = ProjectRoute(order, project);
            var names = baseline.Keys.ToDictionary(path => path, StringComparer.OrdinalIgnoreCase);
            var files = WorkflowOfficeProjectPaths.LocalWorkingFiles(folder, token, requireUniqueNames: true)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var problems = new List<string>();
            var sent = 0;
            var refreshedReport = false;
            foreach (var path in files)
            {
                token.ThrowIfCancellationRequested();
                var target = names.GetValueOrDefault(path, path);
                var expected = baseline.GetValueOrDefault(target);
                string hash;
                try
                {
                    var source = Child(folder, path);
                    hash = (await WorkflowOfficeProjectPaths.FingerprintAsync(source, token)).Hash;
                    if (expected == hash) continue;
                    if (!refreshedReport && verifyProject is not null && !path.Contains('/') &&
                        WorkflowReportFiles.IsReport(path, verifyProject.ImportProfile))
                    {
                        var current = await ReadActiveOrderForWorkingFilesAsync(order, token);
                        if (!current.ActiveProjects.Any(candidate => candidate.Id == project))
                            throw new WorkflowValidationException("Проект больше недоступен в активных заявках. Обновите журнал.");
                        reportName = WorkflowOfficeProjectPaths.AcceptedReportName(current, project);
                        refreshedReport = true;
                    }
                    if (path.Equals(reportName, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"Спецификация «{path}» изменена дома. Нажмите «Рассчитать», чтобы сохранить её в офисе.");
                        continue;
                    }
                    // The file endpoint checks the baseline at replacement time. A sibling file is irrelevant.
                    if (await TryPublishWorkingFileAsync(route, target, folder, source, hash, expected, token)) sent++;
                    else if (!await OfficeFileMatchesAsync(route, target, hash, token))
                    {
                        problems.Add($"Файл «{path}» изменился в офисе. Откройте папку проекта из заявки, чтобы сравнить версии.");
                        continue;
                    }
                }
                catch (WorkflowOfficeSyncUnavailableException ex) { return new(sent, problems.ToArray(), ex.Message); }
                catch (Exception ex) when (IsCopyUnavailable(ex) || ex is WorkflowConflictException or HttpRequestException ||
                    ex is OperationCanceledException && !token.IsCancellationRequested)
                {
                    problems.Add(ex is WorkflowValidationException validation ? $"«{path}» — {validation.Message}" :
                        $"Файл «{path}» не удалось проверить или отправить. Закройте его и проверьте связь с офисом.");
                    continue;
                }
                baseline[target] = hash;
                await SaveBaselineAsync(folder, baseline, token);
            }
            return new(sent, problems.ToArray());
        }
        finally { _files.Release(); }
    }

    private async Task<bool> TryPublishWorkingFileAsync(string route, string path, string folder, string source, string hash, string? expectedHash,
        CancellationToken token)
    {
        var staging = WorkflowOfficeProjectPaths.CreateStateFolder(folder);
        await using var stage = new FileStream(Path.Combine(staging, Guid.NewGuid().ToString("N") + ".tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        await using (var file = File.OpenRead(source))
            await file.CopyToAsync(stage, token);
        stage.Position = 0;
        if (Convert.ToHexString(await SHA256.HashDataAsync(stage, token)) != hash)
            throw new WorkflowValidationException("Файл изменился при подготовке к отправке. Завершите сохранение и повторите действие.");
        stage.Position = 0;
        using var request = Request(HttpMethod.Put, FileRoute(route, path));
        request.Headers.Add(WorkflowOfficeProjectPaths.WorkingFileUploadHeader, "1");
        if (expectedHash is null) request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        else request.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{expectedHash}\""));
        request.Content = new StreamContent(stage);
        return await WithProjectDeadlineAsync(async deadline =>
        {
            using var response = await SendAsync(fileHttp ?? FileTransport, request, deadline);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed) return false;
            await CheckWorkingFileResponseAsync(response, deadline);
            FileConfirmation? confirmation = null;
            try { confirmation = await ReadAsync<FileConfirmation>(response, deadline); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
            if (confirmation?.Sha256 != hash || confirmation.Path != path)
                throw new WorkflowValidationException("Сервер не подтвердил версию файла. Проверьте офисную копию перед повторной отправкой.");
            return true;
        }, token, TimeSpan.FromMinutes(5), workingFiles: true);
    }

    private Task<bool> OfficeFileMatchesAsync(string route, string path, string hash, CancellationToken token) =>
        WithProjectDeadlineAsync(async deadline =>
        {
            using var request = Request(HttpMethod.Get, FileRoute(route, path));
            using var response = await SendAsync(fileHttp ?? FileTransport, request, deadline);
            if (response.StatusCode == HttpStatusCode.NotFound) return false;
            await CheckWorkingFileResponseAsync(response, deadline);
            await using var content = await response.Content.ReadAsStreamAsync(deadline);
            return Convert.ToHexString(await SHA256.HashDataAsync(content, deadline)) == hash;
        }, token, TimeSpan.FromMinutes(5), workingFiles: true);

    private static Task CheckWorkingFileResponseAsync(HttpResponseMessage response, CancellationToken token) => response.StatusCode switch
    {
        HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout =>
            throw new WorkflowOfficeConnectionLostException("Офисный сервер временно недоступен. Повторите действие после восстановления связи."),
        HttpStatusCode.Unauthorized => throw new WorkflowOfficeSyncUnavailableException(
            "Срок подключения истёк. Откройте «Настройки → Хранилище» и подключитесь заново."),
        HttpStatusCode.Forbidden => throw new WorkflowOfficeSyncUnavailableException(
            "На сервере выключено удалённое сохранение. Проверьте настройки сервера."),
        _ => CheckAsync(response, token)
    };

    private string CopyIndexFolder => Path.Combine(profileDirectory, "office-projects", settings.DatabaseId.ToString("N"));
    private string DefaultCopyFolder(Guid project) => Path.Combine(CopyIndexFolder, project.ToString("N"));

    private static FileStream LockCopy(string originalFolder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(originalFolder)!);
        var path = originalFolder + ".lock";
        var lease = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        WorkflowServiceFiles.TryHide(path);
        return lease;
    }

    private (string Folder, string? NewLocation, bool HasCopy) ResolveCopyFolder(string originalFolder, string? copiesRoot, string? nativePath)
    {
        var location = originalFolder + ".location";
        var project = Path.GetFileName(originalFolder);
        var database = settings.DatabaseId.ToString("N");
        if (ReadCopyLocation(location) is { } folder)
        {
            if (!OwnsCopy(folder, originalFolder))
                throw new WorkflowValidationException($"Локальная копия проекта недоступна. Проверьте папку «{folder}».");
            return (folder, null, true);
        }
        if (Directory.Exists(originalFolder)) return (originalFolder, null, OwnsCopy(originalFolder, originalFolder));

        var root = copiesRoot ?? Path.Combine(profileDirectory, "office-projects");
        var legacy = Path.Combine(root, database, project);
        var selected = Directory.Exists(legacy) ? legacy : MirrorCopyFolder(root, nativePath) ?? legacy;
        return (selected, string.Equals(selected, originalFolder,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? null : location,
            OwnsCopy(selected, originalFolder));
    }

    private static string? ReadCopyLocation(string location)
    {
        if (!File.Exists(location)) return null;
        var folder = File.ReadAllText(location);
        if (!Path.IsPathFullyQualified(folder) || folder.Any(char.IsControl))
            throw new InvalidDataException("Сохранённое расположение локальной копии повреждено.");
        return folder;
    }

    private static string? MirrorCopyFolder(string root, string? nativePath)
    {
        // Use the saved folder date and names, including for projects added to an older order.
        var parts = nativePath?.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: >= 6 } || !DateOnly.TryParseExact($"{parts[^6]}-{parts[^5]}-{parts[^4]}",
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return null;
        return WorkflowOfficeProjectPaths.FileInProject(root, string.Join('/', parts[^6..^1]));
    }

    private string CopyOwner(string originalFolder) => $"{settings.DatabaseId:N}/{Path.GetFileName(originalFolder)}";

    private bool OwnsCopy(string folder, string originalFolder)
    {
        if (!File.Exists(Path.Combine(folder, SnapshotName))) return false;
        var owner = Path.Combine(folder, CopyOwnerName);
        if (File.Exists(owner)) return File.ReadAllText(owner) == CopyOwner(originalFolder);
        return Path.GetFileName(folder) == Path.GetFileName(originalFolder) &&
            Path.GetFileName(Path.GetDirectoryName(folder)) == settings.DatabaseId.ToString("N");
    }

    private static void RecordCopyLocation(string? location, string folder)
    {
        if (location is null) return;
        var temporary = location + ".tmp";
        try
        {
            File.WriteAllText(temporary, folder);
            WorkflowServiceFiles.TryHide(temporary);
            File.Move(temporary, location);
        }
        finally { File.Delete(temporary); }
    }

    private static void HideCopyMarkers(string folder)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException) { return; }
        WorkflowServiceFiles.TryHide(Path.Combine(folder, SnapshotName));
        WorkflowServiceFiles.TryHide(Path.Combine(folder, CopyOwnerName));
    }

    private static string ProjectRoute(Guid order, Guid project) => $"api/v1/storage/orders/{order:D}/projects/{project:D}";
    private static string FileRoute(string route, string path) => $"{route}/file?path={Uri.EscapeDataString(path)}";

    private async Task<ProjectManifest> ManifestAsync(string route, CancellationToken cancellation, string? reportProfile = null,
        string? reportName = null, bool workingFiles = false)
    {
        var path = route + "/files";
        if (reportProfile is not null) path += "?reportProfile=" + Uri.EscapeDataString(reportProfile);
        if (reportName is not null) path += "&reportName=" + Uri.EscapeDataString(reportName);
        using var request = Request(HttpMethod.Get, path);
        var manifest = await SendProjectJsonAsync<ProjectManifest>(request, cancellation, workingFiles);
        ValidateManifest(manifest);
        return manifest;
    }

    private static void ValidateManifest(ProjectManifest manifest)
    {
        if (manifest.Files is null || manifest.Directories is null || manifest.Files.Select(file => file.Path)
                .Concat(manifest.UnavailableFiles ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                manifest.Files.Length + (manifest.UnavailableFiles?.Length ?? 0))
            throw new InvalidDataException("Получен неполный список файлов проекта.");
    }

    private static string Child(string folder, string relative) =>
        WorkflowOfficeProjectPaths.FileInProject(folder, relative) ??
        throw new InvalidDataException("Получено недопустимое имя файла проекта.");

    private async Task DownloadFileAsync(string route, ProjectFile file, string destination, CancellationToken token, bool workingFiles = false)
    {
        using var request = Request(HttpMethod.Get, FileRoute(route, file.Path));
        await WithProjectDeadlineAsync(async deadline =>
        {
            using var response = await SendAsync(fileHttp ?? FileTransport, request, deadline);
            if (workingFiles) await CheckWorkingFileResponseAsync(response, deadline);
            else await CheckAsync(response, deadline);
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await response.Content.CopyToAsync(output, deadline);
            var actual = await WorkflowOfficeProjectPaths.FingerprintAsync(destination, deadline);
            if (actual.Length != file.Length || !actual.Hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new WorkflowValidationException($"Файл «{file.Path}» изменился при скачивании. Повторите действие.");
            return true;
        }, token, TimeSpan.FromMinutes(5), workingFiles);
    }

    private static async Task<Dictionary<string, string>> ReadBaselineAsync(string folder, CancellationToken token)
    {
        await using var input = File.OpenRead(Path.Combine(folder, SnapshotName));
        var saved = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(input, cancellationToken: token);
        return saved is null ? throw new InvalidDataException("Список исходных версий локальной копии повреждён.") :
            new(saved, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task SaveBaselineAsync(string folder, Dictionary<string, string> baseline, CancellationToken cancellation)
    {
        var target = Path.Combine(folder, SnapshotName);
        var staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = File.Open(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WorkflowServiceFiles.TryHide(staging);
                await JsonSerializer.SerializeAsync(output, baseline, cancellationToken: cancellation);
            }
            File.Move(staging, target, overwrite: true);
        }
        finally { File.Delete(staging); }
    }
}
