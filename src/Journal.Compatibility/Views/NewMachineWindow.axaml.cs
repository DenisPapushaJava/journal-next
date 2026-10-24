using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Desktop.Controls;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class NewMachineWindow : Window
{
    private NewMachineViewModel? Model => DataContext as NewMachineViewModel;
    private bool _discard;
    public NewMachineWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e); WindowSizing.FitToWorkingArea(this); MachineNameInput.Focus();
        if (Model is { } model) model.Saved += OnSaved;
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Model is { CreatedMachine: null } model)
        {
            if (model.IsSaving) e.Cancel = true;
            else if (model.HasChanges && !_discard) { e.Cancel = true; model.ConfirmDiscard = true; }
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        if (Model is { } model) { model.ReloadCommand.Cancel(); model.Saved -= OnSaved; }
        base.OnClosed(e);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    { base.OnKeyDown(e); if (e.Key == Key.Escape && Model?.IsSaving != true) { Close(); e.Handled = true; } }
    private void OnSaved(object? sender, EventArgs e) => Close(Model?.CreatedMachine);
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
    private void OnKeepEditingClick(object? sender, RoutedEventArgs e) { if (Model is { } model) model.ConfirmDiscard = false; }
    private void OnDiscardClick(object? sender, RoutedEventArgs e) { _discard = true; Close(); }
    private void OnRemoveRateClick(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: PriceRowViewModel row }) Model?.RemoveRate(row); }
}
