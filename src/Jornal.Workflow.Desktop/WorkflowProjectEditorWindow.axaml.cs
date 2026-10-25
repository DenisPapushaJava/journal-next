using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowProjectEditorWindow : Window
{
    public WorkflowProjectEditorWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); FocusEditor(); }
    private void FocusEditor() => Dispatcher.UIThread.Post(() =>
    {
        if (!IsVisible || OwnedWindows.Count > 0) return;
        if (DataContext is WorkflowProjectEditorViewModel { CanEdit: true }) EditWorkflowProjectThickness.Focus();
        else SaveWorkflowProjectParameters.Focus();
    });
    protected override void OnClosing(WindowClosingEventArgs e)
    { if (DataContext is WorkflowProjectEditorViewModel { IsBusy: true } || OwnedWindows.Count > 0) e.Cancel = true; base.OnClosing(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (e.Key == Key.Escape && DataContext is WorkflowProjectEditorViewModel { IsBusy: false } && OwnedWindows.Count == 0) { Close(); e.Handled = true; } }
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not WorkflowProjectEditorViewModel { CanSave: true } model || OwnedWindows.Count > 0) return;
        do
        {
            await model.SaveCommand.ExecuteAsync(null);
            if (model.WasSaved) { Close(); return; }
            if (model.FileFailure is not { } failure) return;
            var dialog = new WorkflowProjectActionWindow("Не удалось изменить проект", failure.Message,
                failure.ProjectPath, "Повторить", "Вернуться к редактированию") { RequestedThemeVariant = ActualThemeVariant };
            if (!await dialog.ShowDialog<bool>(this)) { FocusEditor(); return; }
        } while (model.CanSave);
    }
}
