using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Jornal.Desktop.Views;

namespace Jornal.Desktop.Startup;

public sealed class DesktopStartup
{
    public static TimeSpan MinimumDisplayTime { get; } = TimeSpan.FromSeconds(2);
    private readonly Func<MainWindow> _createMainWindow;
    private readonly Action<Window> _setMainWindow;
    private bool _closed;
    private bool _started;
    private long _shownAt;
    private CancellationTokenSource? _startupCancellation;

    public SplashWindow Splash { get; } = new();
    public Task? Completion { get; private set; }

    public DesktopStartup(Func<MainWindow> createMainWindow, Action<Window> setMainWindow)
    {
        _createMainWindow = createMainWindow;
        _setMainWindow = setMainWindow;
        Splash.Closed += (_, _) => { _closed = true; _startupCancellation?.Cancel(); };
        // Сначала даём Avalonia обработать отрисовку заставки, затем строим рабочее окно.
        Splash.Opened += (_, _) =>
        {
            _shownAt = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(() => Completion = OpenMainWindowAsync(), DispatcherPriority.Background);
        };
    }

    private async Task OpenMainWindowAsync()
    {
        if (_closed || _started) return;
        _started = true;
        using var cancellation = new CancellationTokenSource();
        _startupCancellation = cancellation;
        var token = cancellation.Token;
        MainWindow? main = null;
        try
        {
            main = _createMainWindow();
            // Задаём состояние до первого показа, чтобы не разворачивать уже видимое окно.
            main.WindowState = WindowState.Maximized;
            Splash.ShowLoadingOrders();
            await main.LoadInitialOrdersAsync(token);
            Splash.ShowOpeningJournal();
            var remaining = MinimumDisplayTime - Stopwatch.GetElapsedTime(_shownAt);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
            token.ThrowIfCancellationRequested();
            // Подменяем MainWindow до закрытия заставки, в том числе для OnMainWindowClose.
            _setMainWindow(main);
            main.Show();
            Splash.Close();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            main?.Close();
        }
        catch (Exception exception)
        {
            AppDiagnostics.Capture(DiagnosticOperation.Startup, exception);
            _setMainWindow(Splash);
            main?.Close();
            Splash.ShowStartupError();
        }
        finally { _startupCancellation = null; }
    }
}
