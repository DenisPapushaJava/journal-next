using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

// One latest value, one pending UI callback, at most 10 deliveries/second. No per-file dispatch queue.
internal sealed class WorkflowShopProgressFeed : IProgress<WorkflowShopProgress>, IDisposable
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Action<WorkflowShopProgress> _publish;
    private readonly Timer _timer;
    private WorkflowShopProgress? _latest;
    private int _posted, _disposed;
    public WorkflowShopProgressFeed(Action<WorkflowShopProgress> publish)
    {
        _publish = publish;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }
    public void Report(WorkflowShopProgress value)
    { if (Volatile.Read(ref _disposed) == 0) Interlocked.Exchange(ref _latest, value); }
    private void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _latest) is null || Interlocked.Exchange(ref _posted, 1) != 0) return;
        if (_context is { } context) context.Post(_ => Publish(), null);
        else Publish();
    }
    private void Publish()
    {
        try
        {
            var value = Interlocked.Exchange(ref _latest, null);
            if (Volatile.Read(ref _disposed) == 0 && value is not null) _publish(value);
        }
        finally { Volatile.Write(ref _posted, 0); }
    }
    public void Dispose()
    { if (Interlocked.Exchange(ref _disposed, 1) == 0) { _timer.Dispose(); Interlocked.Exchange(ref _latest, null); } }
}
