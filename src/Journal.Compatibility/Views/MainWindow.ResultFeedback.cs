using Avalonia.Threading;

namespace Jornal.Desktop.Views;

public partial class MainWindow
{
    private ITimer? _resultFeedbackTimer;
    private long _resultFeedbackGeneration;
    private bool _resultFeedbackClosed;

    private void CancelResultFeedbackExpiration()
    {
        _resultFeedbackGeneration++;
        _resultFeedbackTimer?.Dispose();
        _resultFeedbackTimer = null;
    }

    private void ScheduleResultFeedbackExpiration()
    {
        CancelResultFeedbackExpiration();
        if (_resultFeedbackClosed || _viewModel is not { HasTransientResultFeedback: true } model) return;
        var generation = _resultFeedbackGeneration;
        _resultFeedbackTimer = (_journalRefreshClock ?? TimeProvider.System).CreateTimer(_ =>
            Dispatcher.UIThread.Post(() =>
            {
                // Уже поставленный в очередь вызов не должен скрыть более новое сообщение.
                if (_resultFeedbackClosed || generation != _resultFeedbackGeneration || !ReferenceEquals(model, _viewModel)) return;
                CancelResultFeedbackExpiration();
                if (model.HasTransientResultFeedback) model.ResultMessage = string.Empty;
            }), null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }
}
