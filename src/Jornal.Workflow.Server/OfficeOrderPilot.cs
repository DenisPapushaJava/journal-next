using System.Security.Cryptography;
using System.Text.Json;
using Jornal.Infrastructure.Access;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Server;

internal sealed class OfficeOrderPilot(string? databasePath, byte[] draftHash, IWorkflowAccessTransport transport,
    ILogger<OfficeOrderPilot> logger, OfficeDiagnostics diagnostics)
{
    private static readonly JsonSerializerOptions DraftJson = new(JsonSerializerDefaults.Web);

    public async Task<IResult> PublishAsync(HttpRequest request, CancellationToken token)
    {
        if (request.ContentLength is not { } length || length is <= 0 or > 4096) return Error("invalid-draft-size", 400);
        var body = new byte[(int)length];
        try { await request.Body.ReadExactlyAsync(body, token); }
        catch (EndOfStreamException) { return Error("incomplete-draft", 400); }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), draftHash))
            return Error("draft-not-enabled", 403);

        if (databasePath is null || !File.Exists(databasePath)) return Error("file-unavailable", 503);
        Draft? draft;
        try { draft = JsonSerializer.Deserialize<Draft>(body, DraftJson); }
        catch (JsonException) { return Error("invalid-draft", 400); }
        if (draft is null || string.IsNullOrWhiteSpace(draft.Manager) || string.IsNullOrWhiteSpace(draft.Customer) ||
            string.IsNullOrWhiteSpace(draft.Technologist)) return Error("invalid-draft", 400);

        try
        {
            var saved = await new WorkflowMdbStore(databasePath, transport).ImportEmptyOrderAsync(draft.Id,
                draft.Manager, draft.Customer, draft.Technologist, draft.CreatedAt, token);
            return Results.Ok(new { saved.Id, Number = saved.ReferenceNumber });
        }
        catch (WorkflowConflictException) { return Error("changed-during-write", 409); }
        catch (WorkflowOutcomeUnknownException exception) { return Failed("write-unconfirmed", 409, exception); }
        catch (WorkflowValidationException exception) { return Failed("draft-rejected", 409, exception); }
        catch (Exception exception) when (exception is PlatformNotSupportedException or AccessHostBlockedException or
            IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { return Failed("write-failed", 503, exception); }
    }

    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);

    private IResult Failed(string code, int status, Exception exception)
    {
        logger.LogWarning(exception, "Не удалось сохранить тестовую заявку.");
        diagnostics.Record(code, "publish-order", exception);
        return Error(code, status);
    }

    private sealed record Draft(Guid Id, string Manager, string Customer, string Technologist, DateTimeOffset CreatedAt);
}
