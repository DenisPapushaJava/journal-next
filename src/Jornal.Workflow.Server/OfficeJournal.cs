using System.Text.Json;
using Jornal.Infrastructure.Access;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Server;

internal sealed class OfficeJournal(string? databasePath, string? projectsRoot,
    IWorkflowAccessTransport transport, ILogger<OfficeJournal> logger, OfficeDiagnostics diagnostics)
{
    private const string Ready = "ready";

    public async Task<IResult> CheckAsync(CancellationToken token)
    {
        var (snapshot, database) = await ReadAsync(WorkflowAccessOperation.ReadOfficeRevision, token);
        var projects = string.IsNullOrWhiteSpace(projectsRoot) ? "not-configured" :
            Directory.Exists(projectsRoot) ? "available" : "folder-unavailable";
        var ready = database == Ready && projects == "available";
        return Results.Json(new { ready, database, projects, revision = snapshot?.Revision, schemaVersion = snapshot?.OfficeSchemaVersion },
            statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
    }

    public async Task<IResult> ReadOrdersAsync(CancellationToken token, Guid? databaseId = null)
    {
        var (snapshot, state) = await ReadAsync(WorkflowAccessOperation.ReadOfficeOrders, token, databaseId: databaseId);
        if (snapshot?.OfficeOrders is not { } orders)
            return Results.Json(new { error = snapshot is null ? state : "invalid-response" }, statusCode:
                state == "changed-during-read" ? StatusCodes.Status409Conflict : StatusCodes.Status503ServiceUnavailable);

        return Results.Ok(new { snapshot.Revision, snapshot.TotalCount, snapshot.HasMore, Orders = orders });
    }

    public async Task<(WorkflowOfficeProject[]? Projects, string State)> ReadProjectsAsync(Guid orderId, CancellationToken token, Guid? databaseId = null)
    {
        var (snapshot, state) = await ReadAsync(WorkflowAccessOperation.ReadOfficeProjects, token, [orderId], databaseId);
        return (snapshot?.OfficeProjects, snapshot is not null && snapshot.OfficeProjects is null ? "invalid-response" : state);
    }

    public Task<(WorkflowAccessResponse? Snapshot, string State)> ReadTechnologistsAsync(Guid? databaseId, CancellationToken token) =>
        ReadAsync(WorkflowAccessOperation.ReadOfficeTechnologists, token, databaseId: databaseId);

    private async Task<(WorkflowAccessResponse? Snapshot, string State)> ReadAsync(
        WorkflowAccessOperation operation, CancellationToken token, Guid[]? orderIds = null, Guid? databaseId = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) return (null, "not-configured");
        if (!File.Exists(databasePath)) return (null, "file-unavailable");
        try
        {
            // Inspect can upgrade an MDB. Only use AccessHost's read-only operations here.
            var snapshot = await transport.SendAsync(new(1, operation, databasePath, databaseId, OrderIds: orderIds), token);
            return (snapshot, Ready);
        }
        catch (PlatformNotSupportedException) { return (null, "windows-required"); }
        catch (WorkflowConflictException) { return (null, "changed-during-read"); }
        catch (Exception exception) when (exception is WorkflowValidationException or AccessHostBlockedException or
            IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(exception, "Не удалось прочитать MDB программы «Заявки».");
            diagnostics.Record("mdb-read-failed", operation.ToString(), exception);
            return (null, "read-failed");
        }
    }
}
