
namespace Jornal.Workflow.Desktop;

public enum WorkflowRefreshOutcome { Unchanged, Refreshed, Deferred, Failed }

// One owner/window, UI-thread state; native file notifications are marshalled through dispatch.
public sealed class WorkflowJournalRefresh : IDisposable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly Func<bool, CancellationToken, Task<WorkflowRefreshOutcome>> _refresh;
    private readonly Action<Action> _dispatch;
    private readonly Func<string, Action<bool>, Action, Task<IDisposable>> _subscribe;
    private readonly TimeProvider _clock;
    private readonly long _origin;
    private CancellationTokenSource _lifetime = new();
    private IDisposable? _watcher;
    private string? _path;
    private bool _enabled, _disposed, _running, _starting, _visible, _force;
    private long _generation, _watchEpoch, _signals;
    private TimeSpan _lastAttempt, _lastDataRead, _lastSignal, _firstSignal, _retryAt, _watchRetryAt, _verifyAt;
    private int _failures;
    private TimeSpan Now => _clock.GetElapsedTime(_origin);

    public WorkflowJournalRefresh(Func<bool, CancellationToken, Task<WorkflowRefreshOutcome>> refresh, Action<Action> dispatch,
        TimeProvider? clock = null, Func<string, Action<bool>, Action, Task<IDisposable>>? subscribe = null)
    { _refresh = refresh; _dispatch = dispatch; _clock = clock ?? TimeProvider.System; _origin = _clock.GetTimestamp(); _subscribe = subscribe ?? WorkflowFileWatcher.SubscribeAsync; }

    public void Configure(string? path, bool enabled)
    {
        if (_disposed || _path == path && _enabled == enabled) return;
        ++_generation; ++_watchEpoch; _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new();
        _watcher?.Dispose(); _watcher = null;
        _path = path; _enabled = enabled; _signals = 0; _force = false; _failures = 0;
        _lastAttempt = _lastDataRead = Now; _retryAt = _watchRetryAt = TimeSpan.Zero; _verifyAt = Now + TimeSpan.FromMinutes(5);
    }

    private void Signal(bool force)
    {
        if (_signals++ == 0) _firstSignal = Now;
        _lastSignal = Now; _force |= force;
    }

    private async Task WatchAsync(long generation, long epoch)
    {
        _starting = true;
        try
        {
            var subscription = await _subscribe(_path!,
                force => _dispatch(() => { if (!_disposed && generation == _generation && epoch == _watchEpoch) Signal(force); }),
                () => _dispatch(() =>
                {
                    if (_disposed || generation != _generation || epoch != _watchEpoch) return;
                    ++_watchEpoch; _watcher?.Dispose(); _watcher = null; _watchRetryAt = Now + TimeSpan.FromSeconds(30); Signal(true);
                }));
            if (_disposed || generation != _generation || epoch != _watchEpoch) subscription.Dispose();
            else { _watcher = subscription; Signal(true); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_disposed && generation == _generation) _watchRetryAt = Now + TimeSpan.FromMinutes(5); }
        finally { _starting = false; }
    }

    public async Task TickAsync(bool visible, bool allowed)
    {
        if (_disposed || !_enabled || _path is null) return;
        if (_watcher is null && !_starting && Now >= _watchRetryAt) _ = WatchAsync(_generation, ++_watchEpoch);
        if (visible && !_visible) Signal(false);
        _visible = visible;
        if (!visible || !allowed || _running || Now < _retryAt) return;
        var pending = _signals != 0;
        var quiet = !pending || Now - _lastSignal >= TimeSpan.FromSeconds(3) || Now - _firstSignal >= PollInterval;
        var eventDue = pending && Now - _lastAttempt >= TimeSpan.FromSeconds(3) && Now - _lastDataRead >= PollInterval;
        if (!quiet || !eventDue && Now - _lastAttempt < PollInterval) return;
        var generation = _generation; var signals = _signals;
        var force = _force || Now >= _verifyAt;
        _running = true;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var outcome = await _refresh(force, deadline.Token);
            if (_disposed || generation != _generation) return;
            _lastAttempt = Now;
            if (outcome == WorkflowRefreshOutcome.Failed)
            { _retryAt = Now + TimeSpan.FromSeconds(Math.Min(300, 15 * (1 << Math.Min(_failures++, 5)))); return; }
            if (outcome == WorkflowRefreshOutcome.Deferred) return;
            _retryAt = TimeSpan.Zero; _failures = 0;
            if (_signals == signals) { _signals = 0; _force = false; }
            if (outcome == WorkflowRefreshOutcome.Refreshed) { _lastDataRead = Now; _verifyAt = Now + TimeSpan.FromMinutes(5); }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { if (!_disposed && generation == _generation) _lastAttempt = Now; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (!_disposed && generation == _generation) { _lastAttempt = Now; _retryAt = Now + TimeSpan.FromSeconds(30); } }
        finally { _running = false; }
    }

    public void Dispose()
    { if (_disposed) return; _disposed = true; ++_generation; ++_watchEpoch; _lifetime.Cancel(); _watcher?.Dispose(); _lifetime.Dispose(); }
}
