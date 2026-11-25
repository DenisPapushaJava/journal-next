using System.Collections.Concurrent;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Server;

internal sealed class OfficeShopPublication
{
    private sealed record Lease(Guid Key, WorkflowShopPublicationLock Publication)
    {
        internal SemaphoreSlim RecordGate { get; } = new(1, 1);
    }
    private readonly ConcurrentDictionary<(Guid Database, Guid Order), Lease> _leases = new();

    internal WorkflowShopPublicationLock Verify(Guid database, Guid order, string? key) => GetLease(database, order, key).Publication;

    private Lease GetLease(Guid database, Guid order, string? key)
    {
        if (!Guid.TryParse(key, out var id) || !_leases.TryGetValue((database, order), out var held) || held.Key != id)
            throw new WorkflowValidationException("Связь с сервером при передаче прервалась. Обновите подбор и проверьте готовые папки.");
        return held;
    }

    internal async Task<WorkflowAccessResponse> RecordAsync(Guid database, Guid order, string? key,
        Func<Task<WorkflowAccessResponse>> write, CancellationToken token)
    {
        var held = GetLease(database, order, key);
        await held.RecordGate.WaitAsync(token);
        try
        {
            GetLease(database, order, key);
            return await write();
        }
        finally { held.RecordGate.Release(); }
    }

    internal IResult Open(string path, Guid database, Guid order, Guid owner, HttpContext context)
    {
        var lease = WorkflowShopPublicationLock.Acquire(path, database, order, owner);
        context.Response.RegisterForDispose(lease);
        var id = Guid.NewGuid();
        context.Response.Headers[WorkflowShopPublicationLock.LeaseHeader] = id.ToString("D");
        context.Response.Headers[WorkflowShopPublicationLock.RollbackHeader] = "1";
        return Results.Stream(async stream =>
        {
            using (lease)
            {
                var key = (database, order);
                var held = new Lease(id, lease);
                if (!_leases.TryAdd(key, held)) return;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    deadline.CancelAfter(TimeSpan.FromMinutes(20));
                    using var heartbeat = new PeriodicTimer(TimeSpan.FromSeconds(10));
                    ReadOnlyMemory<byte> signal = new byte[] { 1 };
                    do
                    {
                        await stream.WriteAsync(signal, deadline.Token);
                        await stream.FlushAsync(deadline.Token);
                    } while (await heartbeat.WaitForNextTickAsync(deadline.Token));
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException) { }
                finally
                {
                    _leases.TryRemove(key, out _);
                    // MDB outcome may still be pending when the streaming request closes.
                    await held.RecordGate.WaitAsync(CancellationToken.None);
                    lease.Dispose();
                    held.RecordGate.Release();
                }
            }
        }, "application/octet-stream");
    }
}
