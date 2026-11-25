using System.Security.Cryptography;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Microsoft.Net.Http.Headers;

namespace Jornal.Workflow.Server;

internal sealed class OfficeProjectPilot(string? projectsRoot, OfficeJournal journal, ILogger<OfficeProjectPilot> logger,
    OfficeDiagnostics diagnostics)
{
    private const int FileLimit = 1024 * 1024;
    // Both listeners serve the same projects. Serialize the version check and replacement across them.
    private static readonly SemaphoreSlim Publication = new(1, 1);

    public async Task<IResult> ListAsync(Guid orderId, CancellationToken token)
    {
        var (projects, state) = await journal.ReadProjectsAsync(orderId, token);
        if (projects is null) return DatabaseError(state);

        var enabled = new List<object>();
        foreach (var project in projects)
        {
            try
            {
                if (WorkflowOfficePilotPath.Find(projectsRoot, project.NativePath) is not null)
                    enabled.Add(new { project.Id, project.Name });
            }
            catch (Exception exception) when (FileError(exception))
            {
                logger.LogWarning(exception, "Папка проекта недоступна при проверке обмена.");
                diagnostics.Record("pilot-file-unavailable", "list-files", exception);
            }
        }
        return Results.Ok(new { fileName = WorkflowOfficePilotPath.FileName, projects = enabled });
    }

    public async Task<IResult> DownloadAsync(Guid orderId, Guid projectId, CancellationToken token)
    {
        try
        {
            var (path, error) = await ResolveAsync(orderId, projectId, token);
            if (path is null) return error!;
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            if (file.Length > FileLimit) return Error("file-too-large", 413);
            var data = new byte[(int)file.Length];
            await file.ReadExactlyAsync(data, token);
            return Results.File(data, "application/octet-stream", WorkflowOfficePilotPath.FileName,
                entityTag: new EntityTagHeaderValue(Tag(SHA256.HashData(data))));
        }
        catch (Exception exception) when (FileError(exception)) { return Unavailable("download-file", exception); }
    }

    public async Task<IResult> UploadAsync(Guid orderId, Guid projectId, HttpContext context, CancellationToken token)
    {
        var request = context.Request;
        var versions = request.Headers.IfMatch;
        if (versions.Count == 0) return Error("version-required", 428);
        if (versions.Count != 1 || versions[0] is not { Length: 66 } expected ||
            expected[0] != '"' || expected[^1] != '"' || !expected[1..^1].All(Uri.IsHexDigit))
            return Error("invalid-version", 400);
        if (request.ContentLength is not { } length) return Error("length-required", 411);
        if (length > FileLimit) return Error("file-too-large", 413);

        string? staged = null;
        var operation = "resolve-project";
        try
        {
            var (path, error) = await ResolveAsync(orderId, projectId, token);
            if (path is null) return error!;
            operation = "stage-upload";
            var state = WorkflowOfficeProjectPaths.CreateStateFolder(Path.GetDirectoryName(path)!);
            var stagingPath = Path.Combine(state, "upload-" + Guid.NewGuid().ToString("N") + ".tmp");
            string currentUpload;
            await using (var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 8192, FileOptions.Asynchronous))
            {
                staged = stagingPath;
                await request.Body.CopyToAsync(output, token);
                if (output.Length != length) return Error("incomplete-upload", 400);
                output.Flush(flushToDisk: true);
                output.Position = 0;
                currentUpload = Tag(await SHA256.HashDataAsync(output, token));
            }

            await Publication.WaitAsync(token);
            try
            {
                operation = "check-version";
                var (verifiedPath, verifyError) = await ResolveAsync(orderId, projectId, token);
                if (verifiedPath is null) return verifyError!;
                if (!string.Equals(path, verifiedPath, StringComparison.Ordinal)) return Error("project-moved", 409);
                string serverVersion;
                // Close the read handle before replacing the destination on Windows.
                await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    8192, FileOptions.Asynchronous))
                {
                    if (file.Length > FileLimit) return Error("file-too-large", 413);
                    serverVersion = Tag(await SHA256.HashDataAsync(file, token));
                }
                context.Response.Headers.ETag = serverVersion;
                if (serverVersion != expected) return Error("version-conflict", 412);
                token.ThrowIfCancellationRequested();
                operation = "replace-file";
                if (currentUpload != serverVersion) File.Move(staged, path, overwrite: true);
                context.Response.Headers.ETag = currentUpload;
                return Results.NoContent();
            }
            finally { Publication.Release(); }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 400)
        { return Error("incomplete-upload", 400); }
        catch (Exception exception) when (exception is WorkflowValidationException || FileError(exception))
        { return Unavailable(operation, exception); }
        finally
        {
            if (staged is not null)
            {
                try { File.Delete(staged); }
                catch (Exception exception) when (FileError(exception))
                {
                    logger.LogWarning(exception, "Не удалось очистить временный файл проверки обмена.");
                    diagnostics.Record("pilot-cleanup-failed", "remove-staging-file", exception);
                }
            }
        }
    }

    private async Task<(string? Path, IResult? Error)> ResolveAsync(Guid orderId, Guid projectId, CancellationToken token)
    {
        var (projects, state) = await journal.ReadProjectsAsync(orderId, token);
        if (projects is null) return (null, DatabaseError(state));
        var project = Array.Find(projects, item => item.Id == projectId);
        var path = project is null ? null : WorkflowOfficePilotPath.Find(projectsRoot, project.NativePath);
        return path is null ? (null, Error("pilot-file-unavailable", 404)) : (path, null);
    }

    private static string Tag(byte[] hash) => '"' + Convert.ToHexString(hash) + '"';
    private static bool FileError(Exception exception) => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
    private static IResult DatabaseError(string state) => Error(state, state == "changed-during-read" ? 409 : 503);

    private IResult Unavailable(string operation, Exception exception)
    {
        logger.LogWarning(exception, "Тестовый файл обмена недоступен на этапе {Operation}.", operation);
        diagnostics.Record("pilot-file-unavailable", operation, exception);
        return Error("pilot-file-busy-or-unavailable", 409);
    }
}
