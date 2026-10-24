using Avalonia.Threading;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.Diagnostics;

public sealed class DiagnosticRuntime : IDisposable
{
    // Адрес задаётся разработчиками только после сквозной проверки приёмника. В нём нет секретов.
    public const string VerifiedEndpoint = "https://journal-diagnostics.journal-diagnostics-receiver.workers.dev/v1/reports";
    public static string DefaultDirectory => Path.Combine(Path.GetDirectoryName(Customization.JsonUiPreferencesStore.DefaultPath)!, "Diagnostics");
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public DiagnosticService Service { get; }

    public DiagnosticRuntime()
    {
        Service = new(new(DefaultDirectory), _http, VerifiedEndpoint.Length > 0 ? new(VerifiedEndpoint) : null, AppDiagnostics.AppVersion);
        AppDiagnostics.Current = Service;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandled;
        Service.Mark(DiagnosticOperation.Startup);
        Service.Start();
    }
    private void OnUnhandled(object sender, UnhandledExceptionEventArgs args)
    { if (args.ExceptionObject is Exception exception) AppDiagnostics.Capture(DiagnosticOperation.Unexpected, exception, fatal: true); }
    private void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args) => AppDiagnostics.Capture(DiagnosticOperation.Unexpected, args.Exception);
    private void OnDispatcherUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs args)
    {
        AppDiagnostics.Capture(DiagnosticOperation.Unexpected, args.Exception, fatal: true);
        // Не разрешаем продолжать работу с неизвестным состоянием UI/записи.
    }
    public void Dispose()
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
        TaskScheduler.UnobservedTaskException -= OnUnobserved;
        Dispatcher.UIThread.UnhandledException -= OnDispatcherUnhandled;
        AppDiagnostics.Current = null; Service.Dispose(); _http.Dispose();
    }
}
