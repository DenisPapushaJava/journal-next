using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private async void OnProjectActionsOpened(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout { Target.DataContext: WorkflowProjectRow row } flyout || !row.HasManagerMapAction) return;
        row.SetManagerMapAction(cards: 0, pdf: false);
        if (Model is not { CanUse: true, IsPreparingManagerMaps: false } model || !model.Projects.Contains(row)) return;
        try
        {
            var cards = await Task.Run(() => Jornal.Workflow.Infrastructure.WorkflowManagerMapBundle.CountAvailableCards(row.Project), _lifetime.Token);
            if (flyout.IsOpen && ReferenceEquals(flyout.Target?.DataContext, row) && ReferenceEquals(Model, model) &&
                model.CanUse && !model.IsPreparingManagerMaps && model.Projects.Contains(row))
                row.SetManagerMapAction(cards, model.ManagerMapsAsPdf);
        }
        catch (OperationCanceledException) { }
    }

    private void OnCancelManagerMaps(object? sender, RoutedEventArgs e) => Model?.CancelCurrentManagerMaps();

    private async void OnMergeManagerMaps(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0 ||
            sender is not Control { DataContext: WorkflowProjectRow { CanMergeManagerMaps: true } row }) return;
        await model.MergeManagerMapsAsync(row, _lifetime.Token);
    }

    private async void OnSignProjectMaps(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0 ||
            sender is not Control { DataContext: WorkflowProjectRow { CanSignMaps: true } row }) return;
        await model.SignMapsAsync(row.Project.Id, _lifetime.Token);
    }
}
