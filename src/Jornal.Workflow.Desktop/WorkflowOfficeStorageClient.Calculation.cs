using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed class WorkflowOfficeReportConflictException(Guid orderId, Guid projectId, string fileName)
    : Exception($"Спецификация «{fileName}» изменена и дома, и в офисе. Обе версии сохранены.")
{
    internal Guid OrderId { get; } = orderId;
    internal Guid ProjectId { get; } = projectId;
    internal string FileName { get; } = fileName;
}

internal sealed record WorkflowOfficeReportComparison(Guid OrderId, Guid ProjectId, string FileName,
    string Folder, string OfficeFile, string HomeHash, string OfficeHash);

internal sealed class WorkflowOfficeProjectNotStartedException(string message) : Exception(message);

internal sealed partial class WorkflowOfficeStorageClient
{
    private sealed record OfficeCapabilities(bool GuardedCalculation, bool PlannedBending, bool ShopPublicationJournal = false);
    private volatile OfficeCapabilities? _capabilities;
    private WorkflowOfficeReportComparison? _authorizedReport;

    internal async Task<CuttingProject> PrepareProjectAsync(Guid orderId, WorkflowOfficeProjectPreparation preparation,
        CancellationToken token, Action? beforeSend = null)
    {
        if (preparation.HasPlannedBending) await RequirePlannedBendingSupportAsync(token);
        else await RequireCalculationSupportAsync(token);
        using var request = Request(HttpMethod.Post, $"api/v1/storage/orders/{orderId:D}/projects/prepare");
        request.Content = JsonContent.Create(preparation, options: JsonOptions);
        beforeSend?.Invoke();
        var project = await SendProjectJsonAsync<CuttingProject>(request, token);
        if (project.Id != preparation.ProjectId || project.MachineId != preparation.MachineId ||
            project.ThicknessMm != preparation.ThicknessMm || project.HasPlannedBending != preparation.HasPlannedBending ||
            string.IsNullOrWhiteSpace(project.NativePath))
            throw new InvalidDataException("Сервер вернул другую заготовку проекта.");
        return project;
    }

    internal async Task RequireCalculationSupportAsync(CancellationToken token, bool identifyConnectionLoss = false) =>
        _ = await RequireOfficeCapabilitiesAsync(token, identifyConnectionLoss: identifyConnectionLoss);

    private async Task RequirePlannedBendingSupportAsync(CancellationToken token)
    {
        if (!(await RequireOfficeCapabilitiesAsync(token, refresh: _capabilities is { PlannedBending: false })).PlannedBending)
            throw new WorkflowValidationException("Для отметки гибки до расчёта обновите «Сервер заявок» на рабочем ПК.");
    }

    private async Task<OfficeCapabilities> RequireOfficeCapabilitiesAsync(CancellationToken token, bool refresh = false, bool identifyConnectionLoss = false)
    {
        if (!refresh && _capabilities is { } cached) return cached;
        using var request = Request(HttpMethod.Get, "api/v1/storage/capabilities");
        var capabilities = await WithProjectDeadlineAsync(async deadline =>
        {
            using var response = await SendAsync(http, request, deadline);
            if (response.StatusCode == HttpStatusCode.NotFound) return new OfficeCapabilities(false, false);
            await CheckAsync(response, deadline);
            return await ReadAsync<OfficeCapabilities>(response, deadline);
        }, token, TimeSpan.FromSeconds(30), workingFiles: identifyConnectionLoss);
        if (!capabilities.GuardedCalculation) throw new WorkflowValidationException("Для удалённого расчёта обновите «Сервер заявок» на рабочем ПК.");
        return _capabilities = capabilities;
    }

    internal async Task<(string Folder, HashSet<string> SkippedReports, string[] UnavailableReports)> OpenCalculationFolderAsync(
        Guid orderId, CuttingProject project, string? copiesRoot, CancellationToken token)
    {
        var original = DefaultCopyFolder(project.Id);
        var route = ProjectRoute(orderId, project.Id);
        await _files.WaitAsync(token);
        try
        {
            using var lease = LockCopy(original);
            RecoverInterruptedCleanup(original);
            var (folder, location, hasCopy) = ResolveCopyFolder(original, copiesRoot, project.NativePath);
            if (!hasCopy) CheckNewCopyFolder(folder, copiesRoot);
            var manifest = await ManifestAsync(route, token, project.ImportProfile ?? "");
            bool IsSpecification(string path) => !path.Contains('/') && WorkflowReportFiles.IsReport(path, project.ImportProfile);
            var reports = manifest.Files.Where(file => IsSpecification(file.Path)).ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var unavailable = manifest.UnavailableFiles ?? [];
            var skipped = new HashSet<string>(unavailable, StringComparer.OrdinalIgnoreCase);
            if (!hasCopy)
            {
                await CreateCopyAsync(original, folder, route, new([], reports.Values.ToArray()), token);
                RecordCopyLocation(location, folder);
                return (folder, skipped, unavailable);
            }
            RecordCopyLocation(location, folder);
            HideCopyMarkers(folder);
            var baseline = await ReadBaselineAsync(folder, token);
            string? staging = null;
            foreach (var entry in reports.Values)
            {
                var target = Child(folder, entry.Path);
                var saved = baseline.GetValueOrDefault(entry.Path);
                if (entry.Sha256 == saved && File.Exists(target)) continue;
                var local = File.Exists(target) ? (await WorkflowOfficeProjectPaths.FingerprintAsync(target, token)).Hash : null;
                if (local is not null && local != saved && local != entry.Sha256) continue;
                if (local != entry.Sha256)
                {
                    staging ??= WorkflowOfficeProjectPaths.CreateStateFolder(folder);
                    var stage = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        await DownloadFileAsync(route, entry, stage, token);
                        var latest = File.Exists(target) ? (await WorkflowOfficeProjectPaths.FingerprintAsync(target, token)).Hash : null;
                        if (latest != local) throw new WorkflowValidationException($"Спецификация «{entry.Path}» меняется. Завершите сохранение и повторите расчёт.");
                        if (local is null) File.Move(stage, target, overwrite: false);
                        else await ReplaceLocalFileAsync(stage, target, local);
                    }
                    finally { File.Delete(stage); }
                }
                baseline[entry.Path] = entry.Sha256;
                await SaveBaselineAsync(folder, baseline, token);
            }
            foreach (var saved in baseline.Where(file => IsSpecification(file.Key) && !reports.ContainsKey(file.Key) && !skipped.Contains(file.Key)))
            {
                var local = Child(folder, saved.Key);
                if (File.Exists(local) && (await WorkflowOfficeProjectPaths.FingerprintAsync(local, token)).Hash == saved.Value)
                    skipped.Add(saved.Key);
            }
            return (folder, skipped, unavailable);
        }
        finally { _files.Release(); }
    }

    internal async Task<WorkflowOfficePreparedCalculation> PrepareCalculationAsync(WorkflowOrder order, Guid projectId, Guid actor,
        ReportFileSnapshot? snapshot, ReportValues? values, bool bending, CancellationToken token, bool beforeFirstCalculation = false)
    {
        var route = ProjectRoute(order.Id, projectId);
        if (beforeFirstCalculation) await RequirePlannedBendingSupportAsync(token);
        else await RequireCalculationSupportAsync(token);
        WorkflowOfficeReportInput? input = null;
        if (snapshot is not null)
        {
            if (values is null) throw new InvalidDataException("Не заданы исходные параметры отчёта.");
            var name = await PublishReportAsync(order.Id, projectId, route, snapshot, token);
            input = new(name, snapshot.Sha256, values);
        }
        using var request = Request(HttpMethod.Post, route + "/calculation");
        request.Content = JsonContent.Create(new WorkflowOfficeCalculationPreparation(order.Version, actor, input, bending, beforeFirstCalculation)
            { SupportsPlannedBending = true }, options: JsonOptions);
        var prepared = await SendProjectJsonAsync<WorkflowOfficePreparedCalculation>(request, token, workingFiles: true);
        if (input is not null && (string.IsNullOrEmpty(prepared.SourcePath) || prepared.Report?.Sha256 != input.Sha256))
            throw new InvalidDataException("Сервер вернул другой снимок спецификации.");
        prepared.Report?.Validate();
        return prepared;
    }

    private sealed record FileConfirmation(string Path, string Sha256);

    internal async Task<WorkflowOfficeReportComparison> CompareReportsAsync(WorkflowOfficeReportConflictException conflict,
        string? profile, CancellationToken token)
    {
        var original = DefaultCopyFolder(conflict.ProjectId);
        var route = ProjectRoute(conflict.OrderId, conflict.ProjectId);
        await _files.WaitAsync(token);
        try
        {
            using var lease = LockCopy(original);
            var folder = RequireWorkingCopy(original);
            var home = Child(folder, conflict.FileName);
            var office = await FindOfficeReportAsync(route, profile, conflict.FileName, token) ??
                throw new WorkflowValidationException($"Спецификация «{conflict.FileName}» удалена в офисе.");
            var comparison = await CaptureComparedFilesAsync(folder, home, route, office, token);
            return new(conflict.OrderId, conflict.ProjectId, conflict.FileName, comparison.Folder, comparison.OfficeFile!, comparison.HomeHash, office.Sha256);
        }
        finally { _files.Release(); }
    }

    internal async Task ChooseReportAsync(WorkflowOfficeReportComparison comparison, string? profile, bool useOffice, CancellationToken token)
    {
        var original = DefaultCopyFolder(comparison.ProjectId);
        var route = ProjectRoute(comparison.OrderId, comparison.ProjectId);
        await _files.WaitAsync(token);
        try
        {
            using var lease = LockCopy(original);
            var folder = RequireWorkingCopy(original);
            var home = Child(folder, comparison.FileName);
            var office = await FindOfficeReportAsync(route, profile, comparison.FileName, token);
            if (office?.Sha256 != comparison.OfficeHash || !File.Exists(home) ||
                (await WorkflowOfficeProjectPaths.FingerprintAsync(home, token)).Hash != comparison.HomeHash)
                throw new WorkflowValidationException("Файл снова изменился. Откройте сравнение ещё раз.");
            if (!useOffice)
            {
                _authorizedReport = comparison;
                return;
            }

            await InstallComparedFileAsync(folder, home, comparison.OfficeFile, comparison.OfficeHash, comparison.HomeHash, token);
            var baseline = await ReadBaselineAsync(folder, token);
            baseline[comparison.FileName] = comparison.OfficeHash;
            await SaveBaselineAsync(folder, baseline, token);
            _authorizedReport = null;
        }
        finally { _files.Release(); }
    }

    private static async Task ReplaceLocalFileAsync(string stage, string target, string expectedHomeHash)
    {
        var previous = stage + ".previous";
        File.Replace(stage, target, previous);
        if ((await WorkflowOfficeProjectPaths.FingerprintAsync(previous, CancellationToken.None)).Hash != expectedHomeHash)
            throw new WorkflowValidationException($"Во время обновления файл «{Path.GetFileName(target)}» изменился. Локальная версия сохранена в «{previous}».");
        File.Delete(previous);
    }

    private async Task<ProjectFile?> FindOfficeReportAsync(string route, string? profile, string name, CancellationToken token)
    {
        var manifest = await ManifestAsync(route, token, profile ?? "", name, workingFiles: true);
        if (manifest.UnavailableFiles?.Contains(name, StringComparer.OrdinalIgnoreCase) == true)
            throw new WorkflowValidationException($"Спецификация «{name}» недоступна в офисной папке. Повторите расчёт после сохранения файла.");
        return manifest.Files.SingleOrDefault(f => f.Path.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string> PublishReportAsync(Guid order, Guid project, string projectRoute, ReportFileSnapshot snapshot, CancellationToken token)
    {
        await _files.WaitAsync(token);
        try
        {
            var original = DefaultCopyFolder(project);
            using var lease = LockCopy(original);
            var (folder, _, hasCopy) = ResolveCopyFolder(original, null, null);
            var name = Path.GetFileName(snapshot.SourcePath);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!hasCopy || !string.Equals(snapshot.SourcePath, Child(folder, name), comparison))
                throw new WorkflowValidationException("Повторите подбор в папке локальной копии проекта.");
            var baseline = await ReadBaselineAsync(folder, token);
            var remote = await FindOfficeReportAsync(projectRoute, snapshot.Parsed.Profile, name, token);
            var saved = baseline.GetValueOrDefault(name);
            var authorized = _authorizedReport is { } approval && approval.OrderId == order && approval.ProjectId == project &&
                approval.FileName == name && approval.HomeHash == snapshot.Sha256 && approval.OfficeHash == remote?.Sha256;
            if (remote?.Sha256 != snapshot.Sha256)
            {
                if (!authorized && (remote?.Sha256 != saved || snapshot.Sha256 == saved))
                {
                    if (remote is null) throw new WorkflowValidationException($"Спецификация «{name}» удалена в офисе. Сохраните локальную версию под новым именем и повторите расчёт.");
                    if (snapshot.Sha256 == saved) throw new WorkflowConflictException();
                    throw new WorkflowOfficeReportConflictException(order, project, name);
                }
                using var upload = Request(HttpMethod.Put, FileRoute(projectRoute, remote?.Path ?? name));
                if (remote is null) upload.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
                else upload.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{remote.Sha256}\""));
                // The parser and upload use the same captured bytes.
                upload.Content = new ByteArrayContent(snapshot.Bytes);
                var confirmation = await WithProjectDeadlineAsync(async deadline =>
                {
                    using var response = await SendAsync(fileHttp ?? FileTransport, upload, deadline);
                    if (response.StatusCode == HttpStatusCode.PreconditionFailed) throw new WorkflowConflictException();
                    await CheckWorkingFileResponseAsync(response, deadline);
                    return await ReadAsync<FileConfirmation>(response, deadline);
                }, token, TimeSpan.FromMinutes(5), workingFiles: true);
                if (confirmation.Sha256 != snapshot.Sha256 || confirmation.Path != (remote?.Path ?? name))
                    throw new WorkflowValidationException("Сервер не подтвердил версию спецификации. Проверьте файл перед повтором.");
            }
            if (saved != snapshot.Sha256)
            {
                baseline[name] = snapshot.Sha256;
                await SaveBaselineAsync(folder, baseline, token);
            }
            if (authorized) _authorizedReport = null;
            return remote?.Path ?? name;
        }
        finally { _files.Release(); }
    }

    private Task<T> SendProjectJsonAsync<T>(HttpRequestMessage request, CancellationToken token, bool workingFiles = false) => WithProjectDeadlineAsync(async deadline =>
    {
        using var response = await SendAsync(http, request, deadline);
        if (workingFiles) await CheckWorkingFileResponseAsync(response, deadline);
        else await CheckAsync(response, deadline);
        return await ReadAsync<T>(response, deadline);
    }, token, TimeSpan.FromSeconds(55), workingFiles);

    private static async Task<T> WithProjectDeadlineAsync<T>(Func<CancellationToken, Task<T>> request, CancellationToken token,
        TimeSpan timeout, bool workingFiles = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try { return await request(deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (workingFiles) throw new WorkflowOfficeConnectionLostException("Офисный сервер не ответил вовремя. Проверьте связь с офисом.");
            throw new WorkflowValidationException("Сервер не ответил вовремя. Проверьте соединение и повторите действие.");
        }
        catch (HttpRequestException)
        {
            if (workingFiles)
            {
                token.ThrowIfCancellationRequested();
                throw new WorkflowOfficeConnectionLostException("Офисный сервер недоступен. Проверьте подключение ZeroTier.");
            }
            throw new WorkflowValidationException("Сервер недоступен. Проверьте подключение ZeroTier и повторите действие.");
        }
    }
}
