using Avalonia.Controls;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowUpdatesPanel : UserControl
{
    public WorkflowUpdatesPanel() => InitializeComponent();
    public void FocusCheck() => CheckWorkflowUpdates.Focus();
    private async void OnOpenReleases(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AppUpdatesViewModel { IsApplying: false } model ||
            WorkflowIdentity.UpdatesRepository is not { } repository || TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            if (!await top.Launcher.LaunchUriAsync(WorkflowIdentity.ReleasesPageFor(repository)))
                model.Error = $"Не удалось открыть выпуски программы «{WorkflowIdentity.Name}».";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { model.Error = $"Не удалось открыть выпуски программы «{WorkflowIdentity.Name}»."; }
    }
}
