using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private void OnRefreshCalculationPrices(object? sender, RoutedEventArgs e) => Model?.RefreshRateCommand.Execute(null);

    private async void OnEditProject(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0 ||
            sender is not Control { DataContext: WorkflowProjectRow row }) return;
        model.SelectedProject = row;
        if (model.CreateProjectEditor(row.Project.Id) is not { } editor) return;
        await new WorkflowProjectEditorWindow { DataContext = editor, RequestedThemeVariant = ActualThemeVariant }.ShowDialog(this);
        WorkflowProjectsGrid.Focus();
    }

    private void OnRedoProject(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model || sender is not Control { DataContext: WorkflowProjectRow row }) return;
        model.SelectedProject = row; model.RedoProjectCommand.Execute(null); OnFocusProject(sender, e);
    }

    private void OnSelectProjectForTotal(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model || sender is not Control { DataContext: WorkflowProjectRow row }) return;
        model.SelectedProject = row; model.SelectProjectForTotalCommand.Execute(null);
    }

    private async void OnTrashProject(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0 ||
            sender is not Control { DataContext: WorkflowProjectRow row }) return;
        var dialog = new WorkflowProjectActionWindow("Убрать проект в корзину?",
            "Проект исчезнет из рабочего списка и перестанет участвовать в итоге. Его можно будет восстановить вместе с расчётом. Папка и все файлы останутся на своих местах.",
            row.Name, "В корзину") { RequestedThemeVariant = ActualThemeVariant };
        if (await dialog.ShowDialog<bool>(this)) await model.SetProjectTrashAsync(row.Project.Id, true);
        WorkflowProjectsGrid.Focus();
    }

    private async void OnRestoreProject(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is { CanUse: true } model && sender is Control { DataContext: WorkflowProjectRow row })
            await model.SetProjectTrashAsync(row.Project.Id, false);
    }

    private async void OnProjectIncluded(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not CheckBox { DataContext: WorkflowProjectRow row } box) return;
        var included = box.IsChecked == true;
        if (Model is { CanUse: true } model) await model.SetProjectIncludedAsync(row.Project.Id, included);
        if (ReferenceEquals(box.DataContext, row)) box.SetCurrentValue(ToggleButton.IsCheckedProperty, row.DisplayedIncluded);
    }
}
