using Avalonia;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == WorkflowWordMapProcess.Argument)
        {
            Environment.ExitCode = args.Length == 2 ? WorkflowWordMapProcess.RunWorker(args[1]) : 1;
            return;
        }
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        using var http = new HttpClient();
        using var diagnostics = new DiagnosticService(new(WorkflowIdentity.DiagnosticsDirectory), http, null, WorkflowIdentity.Version);
        AppDiagnostics.Current = diagnostics; diagnostics.Start();
        using var guard = OperatingSystem.IsWindows() ? new Mutex(false, @"Local\DenisPapusha.JournalWorkflow.Running") : null;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception exception) { AppDiagnostics.Capture(DiagnosticOperation.Unexpected, exception, fatal: true); throw; }
        finally { AppDiagnostics.Current = null; }
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
