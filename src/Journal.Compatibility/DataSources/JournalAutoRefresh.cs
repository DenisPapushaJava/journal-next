using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.DataSources;

public enum JournalRefreshOutcome { Deferred, Refreshed, Failed }

/// <summary>Один координатор на окно. Все переходы состояния выполняются в UI-потоке.</summary>
public sealed class JournalAutoRefresh : IDisposable
{
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan VerificationInterval = TimeSpan.FromMinutes(5);
    private readonly Func<CancellationToken, Task<JournalRefreshOutcome>> _refresh;
    private readonly Func<string, Action, Action, Task<IDisposable>> _subscribe;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _clock;
    private readonly long _origin;
    private TimeSpan Now => _clock.GetElapsedTime(_origin);
    private string? _path;
    private bool _enabled, _disposed, _running, _wasVisible, _watchStarting, _watchFaulted;
    private long _generation, _watchEpoch, _changes, _processedChanges;
    private int _failures;
    private TimeSpan _lastSuccess, _firstChange, _lastChange, _retryAt, _watchRetryAt;
    private TimeSpan? _lastAttempt;
    private IDisposable? _watcher;
    private CancellationTokenSource _lifetime = new();
    private long? _manualRead;
    private long _manualChanges;

    public JournalAutoRefresh(Func<CancellationToken, Task<JournalRefreshOutcome>> refresh, Action<Action> dispatch,
        TimeProvider? clock = null, Func<string, Action, Action, Task<IDisposable>>? subscribe = null)
    {
        _refresh = refresh; _dispatch = dispatch; _clock = clock ?? TimeProvider.System;
        _origin = _clock.GetTimestamp();
        _subscribe = subscribe ?? JournalFileWatcher.SubscribeAsync;
    }

    public void Configure(string? path, bool enabled)
    {
        if (_disposed || _path == path && _enabled == enabled) return;
        _generation++;
        _lifetime.Cancel(); _lifetime.Dispose(); _lifetime = new();
        ReleaseWatcher();
        _path = path; _enabled = enabled; _watchStarting = false; _watchFaulted = false;
        _changes = _processedChanges = 0; _failures = 0;
        _lastSuccess = Now; _lastAttempt = null; _retryAt = _watchRetryAt = TimeSpan.Zero;
        _wasVisible = false;
        _manualRead = null;
    }

    public void ManualReadStarted(long id)
    {
        if (_disposed) return;
        _manualRead = id; _manualChanges = _changes;
    }

    public void ManualReadCompleted(long id, bool succeeded)
    {
        if (_disposed || _manualRead != id) return;
        _manualRead = null;
        _lastAttempt = Now;
        if (succeeded)
        {
            _lastSuccess = Now; _failures = 0; _retryAt = TimeSpan.Zero;
            _processedChanges = Math.Max(_processedChanges, _manualChanges);
            if (_changes != _processedChanges) _firstChange = Now;
        }
        else RegisterFailure();
    }

    private void SignalChange()
    {
        var now = Now;
        if (_changes == _processedChanges) _firstChange = now;
        _lastChange = now; _changes++;
    }

    public async Task TickAsync(bool visible, bool canRefresh)
    {
        if (_disposed || !_enabled || _path is null) return;
        var now = Now;
        if (visible && !_wasVisible)
        {
            SignalChange(); _lastChange = now - QuietPeriod;
        }
        _wasVisible = visible;
        if (_watchFaulted)
        {
            _watchFaulted = false; ReleaseWatcher(); _watchRetryAt = now + TimeSpan.FromSeconds(30);
        }
        if (_watcher is null && !_watchStarting && now >= _watchRetryAt) _ = AttachWatcherAsync(_generation, _path);
        if (_running || _manualRead is not null || !visible || !canRefresh || now < _retryAt ||
            _lastAttempt is { } previous && now - previous < MinimumInterval) return;
        var dirty = _changes != _processedChanges;
        var changesReady = dirty && (now - _lastChange >= QuietPeriod || now - _firstChange >= MinimumInterval);
        if (!changesReady && now - _lastSuccess < VerificationInterval) return;
        var generation = _generation; var changes = _changes; var token = _lifetime.Token;
        _running = true;
        try
        {
            var outcome = await _refresh(token);
            if (_disposed || generation != _generation) return;
            if (outcome == JournalRefreshOutcome.Deferred) return;
            _lastAttempt = Now;
            if (outcome == JournalRefreshOutcome.Refreshed)
            {
                _lastSuccess = Now; _failures = 0; _retryAt = TimeSpan.Zero;
                _processedChanges = changes;
                if (_changes != changes) _firstChange = Now;
            }
            else RegisterFailure();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_disposed && generation == _generation)
            { AppDiagnostics.Capture(DiagnosticOperation.OrderRead, exception); _lastAttempt = Now; RegisterFailure(); }
        }
        finally { _running = false; }
    }

    private void RegisterFailure()
    {
        _failures = Math.Min(_failures + 1, 6);
        _retryAt = Now + TimeSpan.FromSeconds(Math.Min(300, 15 * (1 << (_failures - 1))));
    }

    private async Task AttachWatcherAsync(long generation, string path)
    {
        _watchStarting = true;
        var epoch = ++_watchEpoch;
        try
        {
            var queued = 0;
            var faulted = 0;
            void Queue(bool error)
            {
                if (error) Interlocked.Exchange(ref faulted, 1);
                if (Interlocked.Exchange(ref queued, 1) != 0) return;
                try { _dispatch(() =>
                {
                    Interlocked.Exchange(ref queued, 0);
                    var hadError = Interlocked.Exchange(ref faulted, 0) != 0;
                    if (_disposed || generation != _generation || epoch != _watchEpoch) return;
                    _watchFaulted |= hadError; SignalChange();
                }); }
                catch (Exception exception) { AppDiagnostics.Capture(DiagnosticOperation.OrderRead, exception); }
            }
            var watcher = await _subscribe(path, () => Queue(false), () => Queue(true));
            if (_disposed || generation != _generation || epoch != _watchEpoch) { watcher.Dispose(); return; }
            _watcher = watcher;
            // Сверка закрывает промежуток до установки наблюдения, в том числе после разрыва сети.
            SignalChange();
        }
        catch (Exception exception)
        {
            if (!_disposed && generation == _generation)
            {
                _watchRetryAt = Now + VerificationInterval;
                AppDiagnostics.Capture(DiagnosticOperation.OrderRead, exception);
            }
        }
        finally { if (generation == _generation) _watchStarting = false; }
    }

    private void ReleaseWatcher()
    {
        _watchEpoch++;
        try { _watcher?.Dispose(); }
        catch (Exception exception) { AppDiagnostics.Capture(DiagnosticOperation.OrderRead, exception); }
        finally { _watcher = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++;
        _lifetime.Cancel(); _lifetime.Dispose(); ReleaseWatcher();
    }
}
