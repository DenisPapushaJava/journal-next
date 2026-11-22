using System.Text.Json;
using Jornal.Infrastructure.Access;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Server;

internal sealed class OfficeWorkException(string code, int status) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

internal sealed class OfficeWorkOrders(string? databasePath, OfficeWorkIdentity identity,
    OfficeJournal journal, IWorkflowAccessTransport transport, ILogger<OfficeWorkOrders> logger, OfficeDiagnostics diagnostics)
{
    private Guid Actor => identity.EmployeeId;
    internal bool IsStorage => identity.IsStorage;

    internal async Task<WorkflowOfficeProject> ReadStorageProjectAsync(Guid orderId, Guid projectId, CancellationToken token)
    {
        var (projects, state) = await journal.ReadProjectsAsync(orderId, token, identity.DatabaseId);
        if (projects is null) throw new OfficeWorkException(state, state == "changed-during-read" ? 409 : 503);
        return projects.SingleOrDefault(project => project.Id == projectId)
            ?? throw new OfficeWorkException("project-folder-unavailable", 404);
    }

    public async Task<IResult> ListAsync(CancellationToken token)
    {
        var (snapshot, state) = await journal.ReadTechnologistsAsync(identity.DatabaseId, token);
        if (snapshot?.OfficeTechnologists is not { } employees)
            return Error(state, state == "changed-during-read" ? 409 : 503);
        if (!employees.Any(employee => employee.Id == Actor)) return Error("technologist-unavailable", 403);
        return await journal.ReadOrdersAsync(token, identity.DatabaseId);
    }

    public Task<IResult> DetailsAsync(Guid orderId, CancellationToken token) => RunAsync("read-order", async store =>
    {
        var (book, order) = await ReadAsync(store, orderId, token);
        return Results.Ok(new
        {
            order.Id, Number = order.ReferenceNumber, order.Version, order.Blcb, order.UpdatedAt,
            Customer = book.Customers.Single(c => c.Id == order.CustomerId).Name,
            Manager = book.Employees.Single(e => e.Id == order.ManagerId).Name,
            Technologist = book.Employees.Single(e => e.Id == order.TechnologistId).Name,
            CanEdit = order.TechnologistId == Actor,
            Projects = order.ActiveProjects.Where(p => p.NativePath is not null).Select(p => new { p.Id, p.Name, p.ThicknessMm })
        });
    });

    public Task<IResult> CreateAsync(NewOrder request, CancellationToken token) => RunAsync("create-order", async store =>
    {
        var saved = await store.CreateRemoteOrderAsync(request.Id, request.Manager, request.Customer, Actor,
            request.CreatedAt ?? DateTimeOffset.Now, token);
        return Results.Ok(new { saved.Id, Number = saved.ReferenceNumber, saved.Version });
    });

    public Task<IResult> UpdateAsync(Guid orderId, OrderEdit request, CancellationToken token) => RunAsync("update-order", async store =>
    {
        var snapshot = await store.WriteOrderAsync(orderId, request.Version, book =>
        {
            var row = book.Orders.Single(order => order.Id == orderId);
            RequireEditor(book, row);
            return WorkflowChanges.UpdateOrderFromManagerName(book, row.Id, request.Version, request.Manager,
                Actor, request.Blcb, Actor, DateTimeOffset.Now);
        }, token);
        var saved = snapshot.Orders.Single(order => order.Id == orderId);
        return Results.Ok(new { saved.Id, Number = saved.ReferenceNumber, saved.Version });
    });

    public async Task<(WorkflowWorkspace Book, WorkflowOrder Order)> ReadAsync(WorkflowMdbStore store, Guid orderId, CancellationToken token)
    {
        var book = await store.ReadOrderAsync(orderId, token);
        RequireActiveActor(book);
        var order = book.Orders.SingleOrDefault(row => row.Id == orderId && !row.IsDeleted)
            ?? throw new OfficeWorkException("order-unavailable", 404);
        return (book, order);
    }

    public void RequireEditor(WorkflowWorkspace book, WorkflowOrder order)
    {
        RequireActiveActor(book);
        if (order.TechnologistId != Actor) throw new OfficeWorkException("assigned-technologist-required", 403);
    }

    private void RequireActiveActor(WorkflowWorkspace book)
    {
        if (!book.Employees.Any(employee => employee.Id == Actor && employee.CanWork))
            throw new OfficeWorkException("technologist-unavailable", 403);
    }

    public async Task<IResult> RunAsync(string operation, Func<WorkflowMdbStore, Task<IResult>> action)
    {
        if (databasePath is null || !File.Exists(databasePath))
            return Error("database-unavailable", 503);
        try { return await action(new WorkflowMdbStore(databasePath, transport, identity.DatabaseId)); }
        catch (OfficeWorkException exception) { return Error(exception.Code, exception.Status); }
        catch (WorkflowConflictException) { return Error("version-conflict", 409); }
        catch (WorkflowOutcomeUnknownException exception) { return Failed("write-unconfirmed", 409, exception); }
        catch (WorkflowValidationException exception) { return Failed("operation-rejected", 409, exception); }
        catch (Exception exception) when (exception is PlatformNotSupportedException or AccessHostBlockedException or
            IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { return Failed("operation-unavailable", 503, exception); }

        IResult Failed(string code, int status, Exception exception)
        {
            logger.LogWarning(exception, "Не удалось выполнить {Operation}.", operation);
            diagnostics.Record(code, operation, exception);
            return Error(code, status);
        }
    }

    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
    internal sealed record NewOrder(Guid Id, string Manager, string Customer, DateTimeOffset? CreatedAt);
    internal sealed record OrderEdit(long Version, string Manager, string? Blcb);
}
