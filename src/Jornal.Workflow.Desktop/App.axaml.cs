using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Jornal.Desktop.ViewModels;
using Jornal.Desktop.Updates;
using Jornal.Workflow.Infrastructure;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Workflow.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var updates = new AppUpdatesViewModel(new WorkflowAppUpdater(WorkflowIdentity.UpdatesRepository),
                preferencesStore: new JsonAppUpdatePreferencesStore(WorkflowIdentity.UpdatePreferencesPath));
            var preferences = new WorkflowPreferencesStore(WorkflowIdentity.PreferencesPath);
            IWorkflowWorkspaceStore store = new LocalWorkflowWorkspaceStore(WorkflowIdentity.WorkspacePath);
            string? error = null;
            try
            {
                var connection = preferences.Load();
                if (connection.DatabasePath is { } path) store = new WorkflowMdbStore(path, databaseId: connection.DatabaseId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
            {
                error = "Не удалось прочитать настройки подключения. Восстановите локальный файл «" + WorkflowIdentity.PreferencesPath + "» и перезапустите программу.";
                store = new WorkflowUnavailableStore(WorkflowIdentity.PreferencesPath, error);
            }
            var model = new WorkflowViewModel(store, updates, AppDiagnostics.Current, preferences);
            desktop.MainWindow = new WorkflowWindow(preferences)
            { DataContext = model, WindowState = Avalonia.Controls.WindowState.Maximized };
            if (error is not null) model.Error = error;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
