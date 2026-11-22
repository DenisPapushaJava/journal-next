using System.Security.Cryptography;
using Jornal.Workflow.Infrastructure;
using Microsoft.AspNetCore.DataProtection;

namespace Jornal.Workflow.Server;

public sealed record OfficeWorkKey(Guid EmployeeId, byte[] Hash);
public sealed record OfficeWorkAccess(Guid? DatabaseId, IReadOnlyList<OfficeWorkKey> Keys, bool AllowWrites = false);

internal sealed class OfficeWorkIdentity
{
    public Guid EmployeeId { get; set; }
    public Guid DatabaseId { get; set; }
    public bool IsStorage { get; set; }
}

internal sealed class OfficeWorkSessions(IDataProtectionProvider provider, byte[] adminKey)
{
    internal sealed record Selection(Guid DatabaseId, Guid TechnologistId);
    private const string Prefix = "session.";
    private readonly ITimeLimitedDataProtector _protector = provider
        .CreateProtector("JournalWorkflowOfficeServer.WorkSession.v1", Convert.ToHexString(SHA256.HashData(adminKey)))
        .ToTimeLimitedDataProtector();

    internal string Create(Guid database, Guid employee) => Prefix +
        _protector.Protect($"{database:N}{employee:N}", TimeSpan.FromDays(30));

    internal string CreateStorage(Guid database) =>
        "storage." + _protector.Protect(database.ToString("N"), TimeSpan.FromDays(30));

    internal bool TryReadStorage(string credential, out Guid database)
    {
        database = Guid.Empty;
        if (credential.Length > 512 || !credential.StartsWith("storage.", StringComparison.Ordinal)) return false;
        try { return Guid.TryParseExact(_protector.Unprotect(credential[8..]), "N", out database) && database != Guid.Empty; }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    internal bool TryRead(string credential, out Guid database, out Guid employee)
    {
        database = employee = Guid.Empty;
        if (credential.Length > 512 || !credential.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var value = _protector.Unprotect(credential[Prefix.Length..]);
            return value.Length == 64 && Guid.TryParseExact(value.AsSpan(0, 32), "N", out database) &&
                Guid.TryParseExact(value.AsSpan(32, 32), "N", out employee) && database != Guid.Empty && employee != Guid.Empty;
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
    }

    internal static async Task<IResult> ListTechnologistsAsync(OfficeJournal journal, CancellationToken token)
    {
        var (snapshot, state) = await journal.ReadTechnologistsAsync(null, token);
        return snapshot?.OfficeTechnologists is { } people
            ? Results.Ok(new WorkflowOfficeTechnologists(snapshot.DatabaseId, people))
            : Unavailable(state);
    }

    internal async Task<IResult> ConnectAsync(Selection selection, OfficeJournal journal, bool allowWrites, CancellationToken token)
    {
        if (selection.DatabaseId == Guid.Empty || selection.TechnologistId == Guid.Empty) return Results.BadRequest();
        var (session, error) = await FindSessionAsync(selection.DatabaseId, selection.TechnologistId, journal, allowWrites, token);
        return session is null ? error! :
            Results.Ok(session with { Token = Create(session.DatabaseId, session.Technologist.Id) });
    }

    internal static async Task<IResult> ReadSessionAsync(OfficeWorkIdentity identity, OfficeJournal journal, bool allowWrites, CancellationToken token)
    {
        var (session, error) = await FindSessionAsync(identity.DatabaseId, identity.EmployeeId, journal, allowWrites, token);
        return session is null ? error! : Results.Ok(session);
    }

    private static async Task<(WorkflowOfficeSession? Session, IResult? Error)> FindSessionAsync(Guid database, Guid employee,
        OfficeJournal journal, bool allowWrites, CancellationToken token)
    {
        var (snapshot, state) = await journal.ReadTechnologistsAsync(database, token);
        if (snapshot?.OfficeTechnologists is not { } people) return (null, Unavailable(state));
        var technologist = people.SingleOrDefault(person => person.Id == employee);
        return technologist is null
            ? (null, Results.Json(new { error = "technologist-unavailable" }, statusCode: 403))
            : (new(database, technologist, allowWrites && snapshot.OfficeSchemaIsCurrent == true,
                ReadOnlyReason: snapshot.OfficeSchemaIsCurrent != true ? "schema-outdated" : allowWrites ? null : "writes-disabled"), null);
    }

    private static IResult Unavailable(string state) =>
        Results.Json(new { error = state }, statusCode: state == "changed-during-read" ? 409 : 503);
}
