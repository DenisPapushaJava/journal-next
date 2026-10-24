using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Desktop.Controls;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class MachineImportWindow : Window
{
    private MachineImportViewModel? Model => DataContext as MachineImportViewModel;
    private bool _discard;
    public MachineImportWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e); WindowSizing.FitToWorkingArea(this); MachineImportProfileInput.Focus();
        if (Model is { } model) model.Saved += OnSaved;
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Model is { SavedMachine: null } model)
        {
            if (model.IsSaving) e.Cancel = true;
            else if (model.HasChanges && !_discard) { e.Cancel = true; model.ConfirmDiscard = true; }
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    { if (Model is { } model) { model.ReloadCommand.Cancel(); model.Saved -= OnSaved; } base.OnClosed(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (!e.Handled && e.Key == Key.Escape && Model?.IsSaving != true) { Close(); e.Handled = true; } }
    private void OnSaved(object? sender, EventArgs e) => Close(Model?.SavedMachine);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
    private void OnKeepEditingClick(object? sender, RoutedEventArgs e) { if (Model is { } model) model.ConfirmDiscard = false; }
    private void OnDiscardClick(object? sender, RoutedEventArgs e) { _discard = true; Close(); }
}
