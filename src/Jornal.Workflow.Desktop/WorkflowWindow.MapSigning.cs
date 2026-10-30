using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private async void OnSignAllMaps(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is { CanSignMaps: true } model && OwnedWindows.Count == 0)
            await model.SignMapsAsync(token: _lifetime.Token);
    }

    private async void OnSignProjectMaps(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0 ||
            sender is not Control { DataContext: WorkflowProjectRow { CanSignMaps: true } row }) return;
        await model.SignMapsAsync(row.Project.Id, _lifetime.Token);
    }
}
