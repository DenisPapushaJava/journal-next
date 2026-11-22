namespace Jornal.Workflow.Server.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--background"])
            return OfficeInstallation.RunBackgroundAsync().GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();
        try
        {
            if (args is ["--install"])
            {
                OfficeInstallation.InstallFromSetupAsync().GetAwaiter().GetResult();
                return 0;
            }
            if (args is ["--uninstall"])
            {
                OfficeInstallation.RemoveSettingsAsync().GetAwaiter().GetResult();
                return 0;
            }

            using var show = new EventWaitHandle(false, EventResetMode.AutoReset, OfficeInstallation.PanelEventName("show"), out var ownsPanel);
            if (!ownsPanel)
            {
                if (args is not ["--tray"]) show.Set();
                return 0;
            }
            using var close = new EventWaitHandle(false, EventResetMode.AutoReset, OfficeInstallation.PanelEventName("close"));
            var startInTray = args is ["--tray"];
            using var panel = new OfficeServerForm(startInTray);
            _ = panel.Handle;
            var showRequest = ThreadPool.RegisterWaitForSingleObject(show, (_, _) => Post(panel, panel.ShowPanel),
                null, Timeout.Infinite, executeOnlyOnce: false);
            var closeRequest = ThreadPool.RegisterWaitForSingleObject(close, (_, _) => Post(panel, panel.RequestClose),
                null, Timeout.Infinite, executeOnlyOnce: true);
            try
            {
                panel.BeginInvoke(new Action(async () => await panel.InitializeAsync(startInTray)));
                Application.Run(panel);
            }
            finally
            {
                showRequest.Unregister(null);
                closeRequest.Unregister(null);
            }
            return 0;
        }
        catch (Exception exception)
        {
            MessageBox.Show(exception.Message, "Сервер заявок", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Post(Control panel, Action action)
    {
        try { panel.BeginInvoke(action); }
        catch (InvalidOperationException) { /* The panel closed while the signal was queued. */ }
    }
}
