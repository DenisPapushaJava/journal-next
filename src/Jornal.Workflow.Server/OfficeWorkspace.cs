using Jornal.Infrastructure.Access;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using System.Security.Cryptography;

namespace Jornal.Workflow.Server;

internal sealed class OfficeWorkspace(string? databasePath, string? projectsRoot, bool allowWrites,
    OfficeWorkIdentity identity, IWorkflowAccessTransport transport, OfficeShopPublication publication)
{
    public Task<IResult> ConnectAsync(OfficeWorkSessions sessions, CancellationToken token) => RunAsync(async path =>
    {
        var state = await transport.SendAsync(new(1, WorkflowAccessOperation.ReadOfficeRevision, path), token);
        if (state.OfficeSchemaVersion is not (>= 6))
            throw new WorkflowValidationException("Для удалённой работы нужна MDB схемы 6 или новее.");
        if (!Directory.Exists(projectsRoot))
            throw new WorkflowValidationException("На сервере недоступна общая папка проектов.");
        return Results.Ok(new { state.DatabaseId, token = sessions.CreateStorage(state.DatabaseId), canWrite = allowWrites });
    });

    public Task<IResult> ExecuteAsync(WorkflowAccessRequest request, string? shopLease, CancellationToken token) => RunAsync(async path =>
    {
        if (request.DatabaseId != identity.DatabaseId || request.Protocol != 1)
            return Error("Запрос относится к другой MDB.", 409);
        var operation = request.Operation;
        var handoff = operation is WorkflowAccessOperation.RecordShopHandoff or WorkflowAccessOperation.RecordProjectHandoffs;
        var clear = operation == WorkflowAccessOperation.ClearShopHandoff;
        if (!handoff && !clear && operation is not (WorkflowAccessOperation.Inspect or WorkflowAccessOperation.Read or WorkflowAccessOperation.ReadOrders or
            WorkflowAccessOperation.ReadRevision or WorkflowAccessOperation.ReadTrash or WorkflowAccessOperation.Apply))
            return Error("Эта операция выполняется в офисных «Заявках».", 400);
        if ((handoff || clear || operation == WorkflowAccessOperation.Apply) && !allowWrites)
            return Error("Удалённое сохранение выключено в панели сервера.", 403);
        if (clear && (request.ClearShopHandoff is not { } clearRequest || clearRequest.OrderId == Guid.Empty))
            return Error("Укажите заявку для снятия отметки передачи.", 400);
        using var clearLease = clear ? WorkflowShopPublicationLock.Acquire(path, identity.DatabaseId, request.ClearShopHandoff!.OrderId) : null;
        if (operation == WorkflowAccessOperation.Apply)
        {
            if (request.Patch is not { } patch || patch.Orders.Any(order => order.Value is null))
                return Error("Окончательное удаление выполняется в офисных «Заявках».", 400);
            if (patch.Catalog is { } catalog)
            {
                var current = await transport.SendAsync(new(1, WorkflowAccessOperation.ReadOrders, path, identity.DatabaseId,
                    OrderIds: [], OfficeSchema6: true), token);
                if (current.Catalog is null || catalog.ProjectRoot != current.Catalog.ProjectRoot)
                    return Error("Общая папка проектов задаётся на сервере.", 400);
            }
            if (patch.Orders.Any(change => change.Value!.Projects.Any(project => OutsideProjectRoot(project.NativePath) ||
                    OutsideProjectRoot(project.Rename?.NativePath)) ||
                change.Value!.Specifications.Any(specification => OutsideProjectRoot(specification.Current.SourcePath) ||
                    OutsideProjectRoot(specification.Current.Report?.ArchivePath))))
                return Error("В заявке есть проект за пределами общей папки сервера.", 400);
        }
        var command = request with
        {
            Path = path, DatabaseId = identity.DatabaseId, OfficeSchema6 = true,
            Operation = operation == WorkflowAccessOperation.Inspect ? WorkflowAccessOperation.ReadOrders : operation,
            OrderIds = operation == WorkflowAccessOperation.Inspect ? [] : request.OrderIds
        };
        if (command.Patch?.OfficeReportVersions is { Length: > 0 } versions)
            return Results.Ok(await ApplyCalculationAsync(command, versions, token));
        if (handoff)
        {
            var orderId = operation == WorkflowAccessOperation.RecordShopHandoff ? command.ShopHandoff?.OrderId : command.ProjectHandoffs?.OrderId;
            if (orderId is not { } id || id == Guid.Empty) return Error("Укажите заявку для передачи в цех.", 400);
            return Results.Ok(await publication.RecordAsync(identity.DatabaseId, id, shopLease, () => transport.SendAsync(command, token), token));
        }
        return Results.Ok(await transport.SendAsync(command, token));
    });

    private async Task<WorkflowAccessResponse> ApplyCalculationAsync(WorkflowAccessRequest command,
        WorkflowOfficeReportVersion[] versions, CancellationToken token)
    {
        if (command.Operation != WorkflowAccessOperation.Apply || command.Patch is not { Orders: [{ Value: { } order }] } patch ||
            versions.Select(version => version.ProjectId).Distinct().Count() != versions.Length)
            throw new WorkflowValidationException("Не удалось проверить версии спецификаций в заявке.");

        var leases = new List<FileStream>(versions.Length);
        try
        {
            foreach (var version in versions)
            {
                var state = order.Specifications.SingleOrDefault(s => s.Id == version.ProjectId)?.Current;
                var project = order.ActiveProjects.SingleOrDefault(p => p.Id == version.ProjectId);
                if (project?.NativePath is not { } native || state is not { SourcePath: { } source, Report: { } report } ||
                    report.Sha256 != version.Sha256 || WorkflowOfficeProjectPaths.InRoot(projectsRoot, native) is not { } folder ||
                    WorkflowOfficeProjectPaths.FileInProject(folder, Path.GetFileName(source)) is not { } workingFile ||
                    !string.Equals(workingFile, Path.GetFullPath(source), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                    !WorkflowReportFiles.IsReport(workingFile, project.ImportProfile))
                    throw new WorkflowValidationException("Не удалось проверить рабочий файл выбранной спецификации.");
                try
                {
                    // On Windows and SMB, keep the verified source readable and prevent replacement during the MDB write.
                    var file = new FileStream(workingFile, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    leases.Add(file);
                    if (Convert.ToHexString(await SHA256.HashDataAsync(file, token)) != version.Sha256)
                        throw new WorkflowConflictException();
                }
                catch (FileNotFoundException) { throw new WorkflowConflictException(); }
                catch (DirectoryNotFoundException) { throw new WorkflowConflictException(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { throw new WorkflowValidationException($"Спецификация «{Path.GetFileName(source)}» занята или недоступна. Повторите расчёт после сохранения файла."); }
            }
            return await transport.SendAsync(command with { Patch = patch with { OfficeReportVersions = null } }, token);
        }
        finally
        {
            foreach (var file in leases) await file.DisposeAsync();
        }
    }

    public Task<IResult> PrepareCalculationAsync(Guid orderId, Guid projectId, WorkflowOfficeCalculationPreparation request, CancellationToken token) => RunAsync(async path =>
    {
        if (!allowWrites) return Error("remote-writes-disabled", 403);
        var snapshot = await transport.SendAsync(new(1, WorkflowAccessOperation.ReadOrders, path, identity.DatabaseId,
            OrderIds: [orderId], OfficeSchema6: true), token);
        var order = snapshot.Orders?.SingleOrDefault(o => o.Id == orderId && !o.IsDeleted && !o.HasProjectRename);
        if (order is null || order.Version != request.OrderVersion) throw new WorkflowConflictException();
        if (snapshot.Catalog?.Employees.Any(e => e.Id == request.ActorId && e.CanWork) != true)
            throw new WorkflowValidationException("Выберите действующего технолога в настройках.");
        if (order.ActiveProjects.SingleOrDefault(p => p.Id == projectId) is not { NativePath: { } native } project ||
            WorkflowOfficeProjectPaths.InRoot(projectsRoot, native) is not { } folder || !Directory.Exists(folder))
            return Error("project-folder-unavailable", 404);

        var before = order.Specifications.SingleOrDefault(s => s.Id == projectId)?.Current;
        if (before is null && project.HasPlannedBending && !request.SupportsPlannedBending)
            throw new WorkflowValidationException(WorkflowProjects.PlannedBendingRequiresUpdatedClient);
        if (request.BeforeFirstCalculation)
        {
            RequirePlannedBendingSchema(snapshot);
            WorkflowProjects.RequireBendingPlanProject(order, projectId);
            if (!request.EnsureBending || request.Report is not null)
                return Error("Некорректный запрос на создание папки ГИБКА.", 400);
            await WorkflowBendingFiles.EnsureAsync(project, token);
            return Results.Ok(new WorkflowOfficePreparedCalculation(null, null));
        }
        var source = before?.SourcePath;
        var report = before?.Report;
        if (request.Report is { } incoming)
        {
            if (incoming.FileName is not { Length: > 0 } name || name.Contains('/') || incoming.Baseline is null ||
                incoming.Sha256 is not { Length: 64 } || !incoming.Sha256.All(char.IsAsciiHexDigit) ||
                WorkflowOfficeProjectPaths.FileInProject(folder, name) is not { } sourcePath || !WorkflowReportFiles.IsReport(sourcePath, project.ImportProfile))
                return Error("Укажите спецификацию из рабочей папки проекта.", 400);
            source = sourcePath;
            // Verify the working bytes even when the immutable archive already exists.
            var archived = await WorkflowReportFiles.ArchiveWorkingReportAsync(source, incoming.Sha256, incoming.Baseline, token);
            report = before?.Report is { } prior && prior.Sha256 == archived.Sha256 && prior.ArchivePath == archived.ArchivePath ? prior : archived;
        }
        else if (before is null) return Error("Сначала подберите спецификацию для проекта.", 400);

        if (before is not null)
        {
            if (OutsideProjectRoot(before.Report?.ArchivePath))
                throw new WorkflowValidationException("Архив предыдущего расчёта находится вне рабочей папки сервера.");
            await WorkflowCalculationHistory.SaveAsync(folder, new(1, order.Id, project.Id, DateTimeOffset.UtcNow,
                request.ActorId, before, null) { OrderVersion = order.Version }, token);
        }
        if (request.EnsureBending) await WorkflowBendingFiles.EnsureAsync(project, token);
        return Results.Ok(new WorkflowOfficePreparedCalculation(source, report));
    });

    public Task<IResult> PrepareProjectAsync(Guid orderId, WorkflowOfficeProjectPreparation request, CancellationToken token) => RunAsync(async path =>
    {
        if (!allowWrites) return RejectProjectPreparation("remote-writes-disabled", 403);
        if (projectsRoot is null || !Directory.Exists(projectsRoot)) return RejectProjectPreparation("project-folder-unavailable", 404);
        var snapshot = await transport.SendAsync(new(1, WorkflowAccessOperation.ReadOrders, path, identity.DatabaseId,
            OrderIds: [orderId], OfficeSchema6: true), token);
        var order = snapshot.Orders?.SingleOrDefault(o => o.Id == orderId && !o.IsDeleted);
        if (order is null || order.Version != request.OrderVersion) throw new WorkflowConflictException();
        if (snapshot.Catalog is not { } catalog || !catalog.Employees.Any(e => e.Id == request.ActorId && e.CanWork))
            return RejectProjectPreparation("Выберите действующего технолога в настройках.");
        if (catalog.Machines.SingleOrDefault(m => m.Id == request.MachineId) is not { } machine)
            return RejectProjectPreparation("Станок не найден. Обновите журнал (F5).");
        if (request.HasPlannedBending && NeedsPlannedBendingSchema(snapshot))
            return RejectProjectPreparation(PlannedBendingSchemaMessage);
        var customer = catalog.Customers.Single(c => c.Id == order.CustomerId).Name;
        return Results.Ok(await WorkflowProjectFiles.PrepareRemoteAsync(projectsRoot, order, customer, machine,
            request.ThicknessMm, request.ItemId, request.ProjectId, DateTimeOffset.Now, token, request.HasPlannedBending));
    });

    private static IResult RejectProjectPreparation(string error, int status = 422) =>
        Results.Json(new { error, projectPreparationRejected = true }, statusCode: status);

    private const string PlannedBendingSchemaMessage = "Для отметки гибки до расчёта откройте MDB в обновлённых «Заявках» на рабочем ПК.";
    private static bool NeedsPlannedBendingSchema(WorkflowAccessResponse snapshot) =>
        snapshot.OfficeSchemaVersion is null or < WorkflowMdbSchema.PlannedBendingVersion;

    private static void RequirePlannedBendingSchema(WorkflowAccessResponse snapshot)
    {
        if (NeedsPlannedBendingSchema(snapshot)) throw new WorkflowValidationException(PlannedBendingSchemaMessage);
    }

    public Task<IResult> OpenShopPublicationAsync(Guid orderId, long version, Guid owner, HttpContext context, CancellationToken token) => RunAsync(async path =>
    {
        if (!allowWrites) return Error("remote-writes-disabled", 403);
        if (owner == Guid.Empty) return Error("Укажите идентификатор компьютера для восстановления передачи.", 400);
        var snapshot = await ReadShopOrderAsync(path, orderId, version, token);
        var order = snapshot.Orders!.Single();
        var catalog = snapshot.Catalog!;
        if (!WorkflowCuttingTask.Prepare(order, catalog.Employees, catalog.Customers, catalog.Machines).IsReady)
            throw new WorkflowValidationException("Заявка не готова к передаче в цех. Проверьте состав и сохранённые расчёты.");
        return publication.Open(path, identity.DatabaseId, orderId, owner, context);
    });

    public Task<IResult> UpdateShopPublicationAsync(Guid orderId, ShopPublicationChange change, string? shopLease, CancellationToken token) => RunAsync(async _ =>
    {
        if (!allowWrites) return Error("remote-writes-disabled", 403);
        await publication.Verify(identity.DatabaseId, orderId, shopLease).UpdateAsync(change, token);
        return Results.NoContent();
    });

    public Task<IResult> ReadShopSourcesAsync(Guid orderId, Guid projectId, long version, string? shopLease, CancellationToken token) => RunAsync(async path =>
    {
        if (shopLease is not null) publication.Verify(identity.DatabaseId, orderId, shopLease);
        var snapshot = await ReadShopOrderAsync(path, orderId, version, token);
        var order = snapshot.Orders!.Single();
        if (!order.Items.Any(item => item.IsIncluded && item.CurrentProjectId == projectId))
            throw new WorkflowConflictException();
        var project = order.ActiveProjects.SingleOrDefault(p => p.Id == projectId);
        var calculation = order.Specifications.SingleOrDefault(s => s.Id == projectId)?.Current;
        var machine = snapshot.Catalog?.Machines.SingleOrDefault(m => m.Id == project?.MachineId);
        if (project?.NativePath is not { } native || calculation is null || machine is null ||
            WorkflowOfficeProjectPaths.InRoot(projectsRoot, native) is not { } folder || !Directory.Exists(folder))
            return Error("project-folder-unavailable", 404);
        var (tree, unavailable) = await WorkflowOfficeProjectPaths.ShopManifestAsync(folder, project, machine, calculation, token);
        if (shopLease is not null) publication.Verify(identity.DatabaseId, orderId, shopLease);
        return Results.Ok(new { tree.Directories, UnavailableFiles = unavailable, Files = tree.Files.Select(f => new { Path = f.RelativePath, f.Length, f.Sha256 }) });
    });

    private async Task<WorkflowAccessResponse> ReadShopOrderAsync(string path, Guid orderId, long version, CancellationToken token)
    {
        var snapshot = await transport.SendAsync(new(1, WorkflowAccessOperation.ReadOrders, path, identity.DatabaseId,
            OrderIds: [orderId], OfficeSchema6: true), token);
        if (snapshot.Orders is not [{ } order] || order.Id != orderId || order.IsDeleted || order.HasProjectRename || order.Version != version)
            throw new WorkflowConflictException();
        if (snapshot.Catalog is null) throw new InvalidDataException("Не получен справочник для заявки.");
        return snapshot;
    }

    private bool OutsideProjectRoot(string? path) =>
        path is not null && WorkflowOfficeProjectPaths.InRoot(projectsRoot, path) is null;

    private async Task<IResult> RunAsync(Func<string, Task<IResult>> operation)
    {
        if (databasePath is null || !File.Exists(databasePath)) return Error("MDB недоступна на сервере.", 503);
        try { return await operation(databasePath); }
        catch (WorkflowConflictException) { return Results.Json(new { conflict = true }, statusCode: 409); }
        catch (WorkflowOutcomeUnknownException) { return Results.Json(new { outcomeUnknown = true }, statusCode: 409); }
        catch (WorkflowValidationException ex) { return Error(ex.Message, 422); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AccessHostBlockedException or PlatformNotSupportedException or InvalidDataException)
        { return Error("Сервер не смог выполнить запрос к MDB.", 503); }
    }

    private static IResult Error(string error, int status) => Results.Json(new { error }, statusCode: status);
}
