using System.Security.Cryptography;
using Jornal.Workflow.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

namespace Jornal.Workflow.Server;

internal sealed class OfficeWorkProjects(string? projectsRoot, OfficeWorkOrders orders, ILogger<OfficeWorkProjects> logger,
    OfficeDiagnostics diagnostics)
{
    private const long FileLimit = 1024L * 1024 * 1024;
    private static readonly SemaphoreSlim Publication = new(1, 1);

    public Task<IResult> ManifestAsync(Guid orderId, Guid projectId, CancellationToken token, string? reportProfile = null, string? reportName = null) =>
        orders.RunAsync("read-project-files", async store =>
        {
            var (folder, name) = await ProjectAsync(store, orderId, projectId, false, token);
            if (reportProfile is not null)
            {
                var (files, unavailable) = await WorkflowOfficeProjectPaths.ReportManifestAsync(folder, reportProfile, reportName, token);
                return Results.Ok(new
                {
                    name, Directories = Array.Empty<string>(), UnavailableFiles = unavailable,
                    Files = files.Select(file => new { Path = file.RelativePath, file.Length, file.Sha256 })
                });
            }
            var contents = await WorkflowOfficeProjectPaths.ManifestAsync(folder, token);
            return Results.Ok(new
            {
                name,
                Directories = contents.Directories.Select(PortablePath),
                Files = contents.Files.Select(file => new { Path = PortablePath(file.RelativePath), file.Length, file.Sha256 })
            });
        });

    public Task<IResult> DownloadAsync(Guid orderId, Guid projectId, string path, CancellationToken token) =>
        orders.RunAsync("download-project-file", async store =>
        {
            var (folder, _) = await ProjectAsync(store, orderId, projectId, false, token);
            var file = Child(folder, path);
            if (!File.Exists(file)) throw new OfficeWorkException("file-unavailable", 404);
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Results.File(stream, "application/octet-stream", Path.GetFileName(file));
        });

    public Task<IResult> UploadAsync(Guid orderId, Guid projectId, string path, HttpRequest request, CancellationToken token) =>
        orders.RunAsync("upload-project-file", async store =>
        {
            var versions = request.Headers.IfMatch;
            var newFile = request.Headers.IfNoneMatch.Count == 1 && request.Headers.IfNoneMatch[0] == "*" && versions.Count == 0;
            var expected = versions.Count == 1 && versions[0] is { Length: 66 } tag && tag[0] == '"' && tag[^1] == '"' &&
                tag[1..^1].All(Uri.IsHexDigit) && request.Headers.IfNoneMatch.Count == 0 ? tag[1..^1].ToUpperInvariant() : null;
            if (!newFile && expected is null) throw new OfficeWorkException("file-version-required", 428);
            if (request.ContentLength is not { } length) throw new OfficeWorkException("length-required", 411);
            if (length > FileLimit) throw new OfficeWorkException("file-too-large", 413);
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
                size.MaxRequestBodySize = FileLimit;

            var (folder, _) = await ProjectAsync(store, orderId, projectId, true, token);
            _ = Child(folder, path);
            var state = WorkflowOfficeProjectPaths.CreateStateFolder(folder);
            var stamp = Guid.NewGuid().ToString("N");
            var stage = Path.Combine(state, stamp + ".tmp");
            var createdStage = false;
            try
            {
                string uploadedHash;
                await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    createdStage = true;
                    await request.Body.CopyToAsync(output, token);
                    if (output.Length != length) throw new OfficeWorkException("incomplete-upload", 400);
                    output.Flush(flushToDisk: true);
                    output.Position = 0;
                    uploadedHash = Convert.ToHexString(await SHA256.HashDataAsync(output, token));
                }

                await Publication.WaitAsync(token);
                try
                {
                    var (latest, _) = await ProjectAsync(store, orderId, projectId, true, token);
                    if (latest != folder) throw new OfficeWorkException("project-moved", 409);
                    var destination = Child(folder, path);
                    if (newFile)
                    {
                        if (File.Exists(destination)) throw new OfficeWorkException("file-version-conflict", 412);
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        _ = Child(folder, path);
                        token.ThrowIfCancellationRequested();
                        try { File.Move(stage, destination, overwrite: false); }
                        catch (IOException) when (File.Exists(destination)) { throw new OfficeWorkException("file-version-conflict", 412); }
                    }
                    else
                    {
                        if (!File.Exists(destination)) throw new OfficeWorkException("file-version-conflict", 412);
                        var original = await WorkflowOfficeProjectPaths.FingerprintAsync(destination, token);
                        if (original.Hash != expected) throw new OfficeWorkException("file-version-conflict", 412);
                        token.ThrowIfCancellationRequested();
                        if (uploadedHash != expected)
                        {
                            var backup = Path.Combine(state, stamp + ".previous");
                            try
                            {
                                File.Replace(stage, destination, backup);
                                var previous = await WorkflowOfficeProjectPaths.FingerprintAsync(backup, CancellationToken.None);
                                if (previous.Hash != expected)
                                    return Results.Json(new { error = "previous-version-needs-review", backup = Path.GetFileName(backup) }, statusCode: 409);
                            }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                            {
                                logger.LogWarning(exception, "Не удалось подтвердить замену файла проекта {ProjectId}.", projectId);
                                diagnostics.Record("file-write-unconfirmed", "replace-project-file", exception);
                                return Results.Json(new { error = "file-write-unconfirmed", backup = Path.GetFileName(backup) }, statusCode: 409);
                            }
                        }
                    }
                    return Results.Ok(new { path, sha256 = uploadedHash });
                }
                finally { Publication.Release(); }
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == 400)
            { throw new OfficeWorkException("incomplete-upload", 400); }
            finally
            {
                if (createdStage)
                {
                    try { File.Delete(stage); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(exception, "Не удалось удалить временный файл загрузки проекта {ProjectId}.", projectId);
                        diagnostics.Record("work-cleanup-failed", "remove-staging-file", exception);
                    }
                }
            }
        });

    private async Task<(string Folder, string Name)> ProjectAsync(WorkflowMdbStore store, Guid orderId, Guid projectId,
        bool edit, CancellationToken token)
    {
        if (orders.IsStorage)
        {
            var remote = await orders.ReadStorageProjectAsync(orderId, projectId, token);
            var location = WorkflowOfficeProjectPaths.InRoot(projectsRoot, remote.NativePath);
            return location is not null && Directory.Exists(location)
                ? (location, remote.Name) : throw new OfficeWorkException("project-folder-unavailable", 404);
        }
        var (book, order) = await orders.ReadAsync(store, orderId, token);
        if (edit) orders.RequireEditor(book, order);
        var project = order.ActiveProjects.SingleOrDefault(row => row.Id == projectId);
        var folder = project?.NativePath is { } native ? WorkflowOfficeProjectPaths.InRoot(projectsRoot, native) : null;
        if (folder is null || project is null || !Directory.Exists(folder))
            throw new OfficeWorkException("project-folder-unavailable", 404);
        return (folder, project.Name);
    }

    private static string Child(string folder, string relative) => WorkflowOfficeProjectPaths.FileInProject(folder, relative)
        ?? throw new OfficeWorkException("invalid-project-path", 400);

    private static string PortablePath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
}
