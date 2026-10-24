using Avalonia.Threading;

namespace Jornal.Desktop.ViewModels;

// Только локальный расчёт после ввода. Поздний callback не обновляет закрытую форму.
internal sealed class PricePreviewScheduler(Action calculate, TimeProvider timeProvider)
{
    private ITimer? _timer;
    private long _generation;
    private bool _stopped;
    internal void Schedule()
    {
        Cancel();
        if (_stopped) return;
        var generation = _generation;
        _timer = timeProvider.CreateTimer(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_stopped || generation != _generation) return;
            Cancel(); calculate();
        }), null, TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
    }
    internal void Cancel() { ++_generation; Interlocked.Exchange(ref _timer, null)?.Dispose(); }
    internal void Stop() { _stopped = true; Cancel(); }
    internal void Start() => _stopped = false;
}
