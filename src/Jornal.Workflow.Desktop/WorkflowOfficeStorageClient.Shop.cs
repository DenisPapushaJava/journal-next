using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using System.Globalization;
using System.Net.Http.Json;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfficeShopFile(Guid ProjectId, string ProjectName, string Path, string HomeHash, string? OfficeHash, string? BaselineHash)
{
    internal WorkflowOfficeFileConflict ToConflict() => new(Path, HomeHash, OfficeHash);
}

internal sealed record WorkflowOfficeShopReview(WorkflowOfficeShopFile[] Files, WorkflowOfficeShopFile? Conflict = null)
{
    internal string Message => Conflict is { } conflict ?
        $"У файла «{conflict.Path}» проекта «{conflict.ProjectName}» разные версии дома и в офисе. Сравните их." :
        $"Есть файлы для отправки в офис: {Files.Length}.";
}

internal sealed class WorkflowOfficeShopReviewRequiredException(WorkflowOfficeShopReview review) : Exception(review.Message)
{
    internal WorkflowOfficeShopReview Review { get; } = review;
}

internal sealed class WorkflowOfficeShopCalculationRequiredException(Guid projectId, string message) : Exception(message)
{
    internal Guid ProjectId { get; } = projectId;
}

internal sealed record WorkflowOfficeShopSnapshot(string Folder, WorkflowOrder Order, Func<ShopTransferSelection, CancellationToken, Task> ValidateProject,
    IReadOnlyDictionary<Guid, string[]> UnavailableFiles)
{
    internal Task DeleteAsync() => DeleteAsync(Folder);

    internal static Task DeleteAsync(string folder) => Task.Run(() =>
    {
        try { Directory.Delete(folder, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex); }
    });
}

internal sealed partial class WorkflowOfficeStorageClient
{
    private OfficeShopLease? _shopLease;
    private sealed record OfficePdfCheck(string Hash, string[] Programs, bool Unrelated);

    private sealed class OfficeShopLease(HttpResponseMessage response, Guid orderId, Guid key, bool supportsRollback,
        WorkflowOfficeStorageClient client) : IWorkflowShopPublication
    {
        public Guid OrderId => orderId;
        public Guid Key => key;
        public bool SupportsRollback => supportsRollback;
        public Task UpdateAsync(ShopPublicationChange change, CancellationToken token) => client.UpdateShopPublicationAsync(this, change, token);
        public void Dispose()
        {
            Interlocked.CompareExchange(ref client._shopLease, null, this);
            response.Dispose();
        }
    }

    internal async Task RequireShopPublicationSupportAsync(CancellationToken token)
    {
        if (!(await RequireOfficeCapabilitiesAsync(token, refresh: _capabilities is { ShopPublicationJournal: false })).ShopPublicationJournal)
            throw new WorkflowValidationException("Для удалённой работы с заданиями в цех обновите «Сервер заявок» на рабочем ПК.");
    }

    internal async Task<IWorkflowShopPublication> OpenShopPublicationAsync(WorkflowOrder order, Guid owner, CancellationToken token)
    {
        using var request = Request(HttpMethod.Post, $"api/v1/storage/orders/{order.Id:D}/shop-publication?version={order.Version.ToString(CultureInfo.InvariantCulture)}&owner={owner:D}");
        return await WithProjectDeadlineAsync<IWorkflowShopPublication>(async deadline =>
        {
            var response = await SendAsync(fileHttp ?? FileTransport, request, deadline);
            try
            {
                await CheckAsync(response, deadline);
                if (!response.Headers.TryGetValues(WorkflowShopPublicationLock.LeaseHeader, out var values) ||
                    !Guid.TryParse(values.SingleOrDefault(), out var key))
                    throw new InvalidDataException("Сервер не подтвердил начало передачи в цех.");
                var stream = await response.Content.ReadAsStreamAsync(deadline);
                var signal = new byte[1];
                if (await stream.ReadAsync(signal, deadline) != 1 || signal[0] != 1)
                    throw new WorkflowValidationException("Сервер прервал подготовку передачи. Обновите подбор.");
                var supportsRollback = response.Headers.TryGetValues(WorkflowShopPublicationLock.RollbackHeader, out var feature) &&
                    feature.SingleOrDefault() == "1";
                var lease = new OfficeShopLease(response, order.Id, key, supportsRollback, this);
                if (Interlocked.CompareExchange(ref _shopLease, lease, null) is not null)
                    throw new WorkflowValidationException("Уже идёт передача в цех. Дождитесь завершения.");
                return lease;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }, token, TimeSpan.FromSeconds(30));
    }

    private Task UpdateShopPublicationAsync(OfficeShopLease lease, ShopPublicationChange change, CancellationToken token) =>
        WithProjectDeadlineAsync(async deadline =>
        {
            using var request = Request(HttpMethod.Post, $"api/v1/storage/orders/{lease.OrderId:D}/shop-publication/project");
            request.Headers.Add(WorkflowShopPublicationLock.LeaseHeader, lease.Key.ToString("D"));
            request.Content = JsonContent.Create(change, options: JsonOptions);
            using var response = await SendAsync(http, request, deadline);
            await CheckAsync(response, deadline);
            return true;
        }, token, TimeSpan.FromSeconds(30));

    private void AddShopLease(HttpRequestMessage request, Guid orderId, bool required)
    {
        if (Volatile.Read(ref _shopLease) is { } lease && lease.OrderId == orderId)
            request.Headers.Add(WorkflowShopPublicationLock.LeaseHeader, lease.Key.ToString("D"));
        else if (required)
            throw new WorkflowValidationException("Связь с сервером при передаче прервалась. Обновите подбор и проверьте готовые папки.");
    }

    internal async Task<WorkflowOfficeShopSnapshot> PrepareShopSourcesAsync(WorkflowOrder order, WorkflowWorkspace workspace, string? copiesRoot,
        IProgress<WorkflowShopProgress> progress, CancellationToken token, WorkflowOfficeShopFile[]? approved = null)
    {
        await RequireShopPublicationSupportAsync(token);
        return await Task.Run(async () =>
        {
            var projects = order.Projects.ToArray();
            var sources = new Dictionary<Guid, (int Index, CuttingProject Project, WorkflowMachine Machine, SpecificationState Calculation, ProjectManifest Manifest)>();
            var officePdfChecks = new Dictionary<(Guid ProjectId, string Path), OfficePdfCheck>();
            var approvals = approved?.ToLookup(file => file.ProjectId);
            var afterUpload = false;
            while (true)
            {
                sources.Clear();
                var pending = new List<WorkflowOfficeShopFile>();
                foreach (var item in order.Items.Where(i => i.IsIncluded).OrderBy(i => i.Number))
                {
                    var index = Array.FindIndex(projects, p => p.Id == item.CurrentProjectId && !p.IsDeleted);
                    if (index < 0) continue;
                    var project = projects[index];
                    if (project.NativePath is not { } native ||
                        order.Specifications.SingleOrDefault(s => s.Id == project.Id)?.Current is not { } calculation)
                        throw new WorkflowValidationException($"Для проекта «{project.Name}» нужен сохранённый расчёт и рабочий файл.");
                    var machine = workspace.Machines.Single(m => m.Id == project.MachineId);
                    progress.Report(new(WorkflowShopStage.Discovery, project.Name, Activity: "Проверяем офисные файлы"));
                    var manifest = await ReadShopManifestAsync(order, project.Id, token);
                    var expected = approvals?[project.Id].ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
                    var changes = await ReadPendingShopFilesAsync(order.Id, project, machine, calculation, copiesRoot, manifest, officePdfChecks, token,
                        expected, afterUpload);
                    if (changes.FirstOrDefault(file => file.OfficeHash != file.BaselineHash) is { } conflict)
                        throw new WorkflowOfficeShopReviewRequiredException(new([], conflict));
                    pending.AddRange(changes);
                    if (!manifest.Files.Any(f => f.Path.Equals(WorkflowOfficeProjectPaths.FileName(native), StringComparison.OrdinalIgnoreCase)))
                        throw new WorkflowValidationException($"Рабочий файл проекта «{project.Name}» не найден на сервере.");
                    if (calculation.Report is { } accepted && (calculation.SourcePath is not { } report ||
                        !manifest.Files.Any(f => f.Path.Equals(WorkflowOfficeProjectPaths.FileName(report), StringComparison.OrdinalIgnoreCase) &&
                            f.Sha256.Equals(accepted.Sha256, StringComparison.OrdinalIgnoreCase))))
                        throw new WorkflowOfficeShopCalculationRequiredException(project.Id,
                            $"Спецификация проекта «{project.Name}» изменилась или недоступна. Обновите расчёт перед передачей.");
                    sources.Add(project.Id, (index, project, machine, calculation, manifest));
                }
                if (pending.Count == 0) break;
                if (afterUpload || approved is null || pending.Except(approved).Any())
                    throw new WorkflowOfficeShopReviewRequiredException(new(pending.ToArray()));
                foreach (var group in pending.GroupBy(file => file.ProjectId))
                {
                    var source = sources[group.Key];
                    await PublishShopFilesAsync(order, source.Project, copiesRoot, source.Manifest, group, progress, token);
                }
                // Re-read the office contents and require the approved versions before building the kit.
                afterUpload = true;
            }

            var folder = Directory.CreateTempSubdirectory("journal-shop-").FullName;
            WorkflowServiceFiles.TryHide(folder);
            var pdfChecks = new Dictionary<(Guid ProjectId, string Path), (string Hash, string InputKey, bool Unrelated)>();
            async Task ValidateProject(ShopTransferSelection selection, CancellationToken cancellation)
            {
                var projectId = selection.Project.Project.Id;
                var source = sources[projectId];
                var current = await ReadShopManifestAsync(order, projectId, cancellation, requireLease: true);
                WorkflowValidationException Changed(string? file = null) =>
                    new(file is null ? $"Состав папок проекта «{source.Project.Name}» изменился в офисе. Обновите подбор." :
                        $"В офисе изменился файл «{file}» проекта «{source.Project.Name}». Обновите подбор.");
                if (!current.Directories.Order(StringComparer.Ordinal).SequenceEqual(source.Manifest.Directories.Order(StringComparer.Ordinal)))
                    throw Changed();
                var unavailable = (source.Manifest.UnavailableFiles ?? []).ToHashSet(StringComparer.Ordinal);
                var stillUnavailable = (current.UnavailableFiles ?? []).ToHashSet(StringComparer.Ordinal);
                if (stillUnavailable.FirstOrDefault(file => !unavailable.Contains(file)) is { } blocked)
                    throw Changed(blocked);
                var previous = source.Manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
                var latest = current.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
                var report = source.Calculation.SourcePath is { } path ? WorkflowOfficeProjectPaths.FileName(path) : null;
                var selected = selection.Files.Select(file => file.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                bool MayIgnorePdf(string name) => Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals(report, StringComparison.OrdinalIgnoreCase) && !selected.Contains(name);

                foreach (var file in current.Files.Where(file => !previous.ContainsKey(file.Path)))
                    if (!selection.Reviewed || !unavailable.Contains(file.Path) || !MayIgnorePdf(file.Path) ||
                        !await IsUnrelatedAsync(file))
                        throw Changed(file.Path);
                foreach (var file in unavailable)
                    if (!stillUnavailable.Contains(file) && !latest.ContainsKey(file) && (!selection.Reviewed || !MayIgnorePdf(file)))
                        throw Changed(file);
                var ignored = selection.Project.Files.Where(f => f.Ignored).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var file in source.Manifest.Files)
                {
                    var replacement = latest.GetValueOrDefault(file.Path);
                    if (file == replacement) continue;
                    if (!ignored.Contains(file.Path) || !MayIgnorePdf(file.Path) ||
                        replacement is not null && !await IsUnrelatedAsync(replacement))
                        throw Changed(file.Path);
                }
                var newChanges = await ReadPendingShopFilesAsync(order.Id, source.Project, source.Machine, source.Calculation, copiesRoot, current,
                    officePdfChecks, cancellation);
                if (newChanges.FirstOrDefault() is { } edit)
                    throw new WorkflowValidationException($"Файл «{edit.Path}» проекта «{edit.ProjectName}» изменился дома. Обновите подбор перед передачей.");

                async Task<bool> IsUnrelatedAsync(ProjectFile file)
                {
                    var key = (projectId, file.Path);
                    if (selection.InputKey is not null && pdfChecks.TryGetValue(key, out var cached) &&
                        cached.Hash == file.Sha256 && cached.InputKey == selection.InputKey)
                        return cached.Unrelated;
                    var checkPath = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".pdf");
                    try
                    {
                        await DownloadFileAsync(ProjectRoute(order.Id, projectId), file, checkPath, cancellation);
                        var unrelated = WorkflowShopDiscovery.IsUnrelatedBlockPdf(checkPath, selection, cancellation);
                        if (selection.InputKey is { } inputKey) pdfChecks[key] = (file.Sha256, inputKey, unrelated);
                        return unrelated;
                    }
                    finally { File.Delete(checkPath); }
                }
            }

            try
            {
                foreach (var (index, project, machine, calculation, manifest) in sources.Values)
                {
                    var nativeName = WorkflowOfficeProjectPaths.FileName(project.NativePath!);
                    var workingFile = manifest.Files.Single(f => f.Path.Equals(nativeName, StringComparison.OrdinalIgnoreCase));
                    var stage = Directory.CreateDirectory(Path.Combine(folder, project.Id.ToString("N"))).FullName;
                    var route = ProjectRoute(order.Id, project.Id);
                    var needed = WorkflowOfficeProjectPaths.ShopSourceFilter(project, machine, calculation, includeAcceptedReport: false);
                    foreach (var directory in manifest.Directories) Directory.CreateDirectory(Child(stage, directory));
                    foreach (var file in manifest.Files)
                    {
                        // The saved report is checked by hash. The cutter needs only working files,
                        // maps, programs and the bending subtree in the temporary source folder.
                        if (!needed(file.Path)) continue;
                        progress.Report(new(WorkflowShopStage.Discovery, project.Name, file.Path, Activity: "Скачиваем файлы для комплекта"));
                        var destination = Child(stage, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        await DownloadFileAsync(route, file, destination, token);
                    }
                    projects[index] = project with { NativePath = Child(stage, workingFile.Path) };
                }
                return new WorkflowOfficeShopSnapshot(folder, order with { Projects = projects }, ValidateProject,
                    sources.Where(pair => pair.Value.Manifest.UnavailableFiles is { Length: > 0 })
                        .ToDictionary(pair => pair.Key, pair => pair.Value.Manifest.UnavailableFiles!));
            }
            catch
            {
                await WorkflowOfficeShopSnapshot.DeleteAsync(folder);
                throw;
            }
        }, token);
    }

    private async Task<ProjectManifest> ReadShopManifestAsync(WorkflowOrder order, Guid projectId, CancellationToken token, bool requireLease = false)
    {
        using var request = Request(HttpMethod.Get, ProjectRoute(order.Id, projectId) + "/shop-sources?version=" +
            order.Version.ToString(CultureInfo.InvariantCulture));
        if (requireLease) AddShopLease(request, order.Id, required: true);
        var manifest = await SendProjectJsonAsync<ProjectManifest>(request, token);
        ValidateManifest(manifest);
        return manifest;
    }

    private async Task<WorkflowOfficeShopFile[]> ReadPendingShopFilesAsync(Guid orderId, CuttingProject project, WorkflowMachine machine,
        SpecificationState calculation, string? copiesRoot, ProjectManifest office, Dictionary<(Guid ProjectId, string Path), OfficePdfCheck> officePdfChecks,
        CancellationToken token,
        IReadOnlyDictionary<string, WorkflowOfficeShopFile>? expected = null, bool afterUpload = false)
    {
        await _files.WaitAsync(token);
        try
        {
            var original = DefaultCopyFolder(project.Id);
            var (folder, _, hasCopy) = ResolveCopyFolder(original, copiesRoot, project.NativePath);
            if (!hasCopy && expected is { Count: > 0 })
                throw new WorkflowValidationException($"Локальная копия проекта «{project.Name}» недоступна. Обновите подбор.");
            if (!hasCopy) return [];
            using var lease = LockCopy(original);
            var baseline = await ReadBaselineAsync(folder, token);
            var remote = office.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var relevant = WorkflowOfficeProjectPaths.ShopSourceFilter(project, machine, calculation);
            var local = IndexWorkingFiles(folder, token, relevant);
            var unavailable = (office.UnavailableFiles ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var report = calculation.SourcePath is { } path ? WorkflowOfficeProjectPaths.FileName(path) : null;
            var pending = new List<WorkflowOfficeShopFile>();
            var baselineChanged = false;
            static bool IsProgram(string name) => !name.Contains('/') &&
                (name.EndsWith(".nc", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".cnc", StringComparison.OrdinalIgnoreCase));
            var names = local.Keys.Concat(baseline.Keys).Concat(expected?.Keys ?? []).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var name in names)
            {
                if (!relevant(name)) continue;
                var officeFile = remote.GetValueOrDefault(name);
                var officeHash = officeFile?.Sha256;
                var homeHash = local.TryGetValue(name, out var localName) ?
                    (await WorkflowOfficeProjectPaths.FingerprintAsync(Child(folder, localName), token)).Hash : null;
                var previous = baseline.GetValueOrDefault(name);
                if (expected?.GetValueOrDefault(name) is { } approved && officeHash != approved.HomeHash &&
                    (afterUpload || homeHash == previous))
                {
                    if (homeHash is not null && homeHash != officeHash && !unavailable.Contains(name))
                    {
                        pending.Add(new(project.Id, project.Name, name, homeHash, officeHash, approved.HomeHash));
                        continue;
                    }
                    throw new WorkflowValidationException($"Подтверждённая версия файла «{name}» проекта «{project.Name}» изменилась или недоступна. Начните подбор заново.");
                }
                if (homeHash == officeHash)
                {
                    if (homeHash != previous && homeHash is not null)
                    {
                        baseline[officeFile!.Path] = homeHash;
                        baselineChanged = true;
                    }
                    continue;
                }
                if (homeHash == previous) continue;
                if (homeHash is null)
                    throw new WorkflowValidationException($"В локальной копии проекта «{project.Name}» удалён файл «{name}». Откройте папку проекта, чтобы восстановить офисную версию.");
                if (name.Equals(report, StringComparison.OrdinalIgnoreCase))
                    throw new WorkflowOfficeShopCalculationRequiredException(project.Id,
                        $"Спецификация «{name}» проекта «{project.Name}» изменена дома. Обновите расчёт перед передачей в цех.");
                if (unavailable.Contains(name))
                    throw new WorkflowValidationException($"Файл «{name}» проекта «{project.Name}» недоступен в офисе. Закройте его и обновите подбор.");
                if (!name.Contains('/') && Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                    WorkflowShopDiscovery.IsUnrelatedBlockPdf(Child(folder, name), project, machine, local.Keys.Where(IsProgram), token))
                {
                    if (officeFile is null || await IsOfficePdfUnrelatedAsync(officeFile)) continue;
                    throw new WorkflowValidationException($"Домашний PDF «{name}» проекта «{project.Name}» не относится к комплекту. В офисе файл с этим именем может быть рабочей картой. Проверьте PDF в папке проекта перед передачей.");
                }
                pending.Add(new(project.Id, project.Name, name, homeHash, officeHash, previous));
            }
            if (baselineChanged) await SaveBaselineAsync(folder, baseline, token);
            return pending.ToArray();

            async Task<bool> IsOfficePdfUnrelatedAsync(ProjectFile file)
            {
                var programs = remote.Keys.Where(IsProgram).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var key = (project.Id, file.Path);
                if (officePdfChecks.TryGetValue(key, out var check) && check.Hash == file.Sha256 &&
                    check.Programs.SequenceEqual(programs, StringComparer.OrdinalIgnoreCase))
                    return check.Unrelated;
                var staging = WorkflowOfficeProjectPaths.CreateStateFolder(folder);
                var candidate = Path.Combine(staging, Guid.NewGuid().ToString("N") + ".pdf");
                try
                {
                    await DownloadFileAsync(ProjectRoute(orderId, project.Id), file, candidate, token);
                    var unrelated = WorkflowShopDiscovery.IsUnrelatedBlockPdf(candidate, project, machine, programs, token);
                    officePdfChecks[key] = new(file.Sha256, programs, unrelated);
                    return unrelated;
                }
                finally { File.Delete(candidate); }
            }
        }
        finally { _files.Release(); }
    }

    private async Task PublishShopFilesAsync(WorkflowOrder order, CuttingProject project, string? copiesRoot, ProjectManifest manifest,
        IEnumerable<WorkflowOfficeShopFile> changes, IProgress<WorkflowShopProgress> progress, CancellationToken token)
    {
        await _files.WaitAsync(token);
        try
        {
            var original = DefaultCopyFolder(project.Id);
            using var lease = LockCopy(original);
            var (folder, location, hasCopy) = ResolveCopyFolder(original, copiesRoot, project.NativePath);
            if (!hasCopy) throw new WorkflowValidationException($"Локальная копия проекта «{project.Name}» недоступна. Обновите подбор.");
            var baseline = await ReadBaselineAsync(folder, token);
            RecordCopyLocation(location, folder);
            var office = manifest.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
            var route = ProjectRoute(order.Id, project.Id);
            foreach (var change in changes)
            {
                if (baseline.GetValueOrDefault(change.Path) != change.BaselineHash) throw LocalFileChanged(change.Path);
                progress.Report(new(WorkflowShopStage.Discovery, project.Name, change.Path, Activity: "Отправляем изменения в офис"));
                var remote = office.GetValueOrDefault(change.Path);
                if (!await TryPublishWorkingFileAsync(route, remote?.Path ?? change.Path, folder, Child(folder, change.Path), change.HomeHash, remote?.Sha256, token))
                {
                    var current = await ReadShopManifestAsync(order, project.Id, token);
                    if ((current.UnavailableFiles ?? []).Contains(change.Path, StringComparer.OrdinalIgnoreCase))
                        throw new WorkflowValidationException($"Файл «{change.Path}» недоступен в офисе. Закройте его и обновите подбор.");
                    var hash = current.Files.SingleOrDefault(file => file.Path.Equals(change.Path, StringComparison.OrdinalIgnoreCase))?.Sha256;
                    if (hash != change.HomeHash)
                        throw new WorkflowOfficeShopReviewRequiredException(new([], change with { OfficeHash = hash }));
                }
                baseline[remote?.Path ?? change.Path] = change.HomeHash;
                await SaveBaselineAsync(folder, baseline, token);
            }
        }
        finally { _files.Release(); }
    }
}
