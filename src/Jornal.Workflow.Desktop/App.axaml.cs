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
            desktop.MainWindow = new WorkflowWindow(new WorkflowPreferencesStore(WorkflowIdentity.PreferencesPath))
            { DataContext = new WorkflowViewModel(new LocalWorkflowWorkspaceStore(WorkflowIdentity.WorkspacePath), updates, AppDiagnostics.Current), WindowState = Avalonia.Controls.WindowState.Maximized };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
