using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.Views;

public partial class PriceEditorWindow : Window
{
    private PriceEditorViewModel? Model => DataContext as PriceEditorViewModel;
    private bool _discard;
    public PriceEditorWindow() => InitializeComponent();
    protected override async void OnOpened(EventArgs e)
    { base.OnOpened(e); if (Model is { } model) { model.PropertyChanged += OnModelChanged; await model.RefreshCommand.ExecuteAsync(null); } }
    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PriceEditorViewModel.IsConfirmingThickness) && Model?.IsConfirmingThickness == true)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => BackFromThicknessConfirmationButton.Focus());
    }
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Model is { } model)
        {
            if (model.IsSaving || model.IsExportDialogOpen) e.Cancel = true;
            else if (model.IsConfirmingThickness) { e.Cancel = true; model.DismissThicknessConfirmation(); }
            else if (model.HasChanges && !_discard) { e.Cancel = true; model.ConfirmDiscard = true; }
        }
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e) { if (Model is { } model) { model.PropertyChanged -= OnModelChanged; model.RefreshCommand.Cancel(); } base.OnClosed(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Model?.IsConfirmingThickness == true)
        { Model.DismissThicknessConfirmation(); SavePricesButton.Focus(); e.Handled = true; return; }
        if (e.Key == Key.Enter && Model?.CanStageRate == true)
        { StageRateAndScroll(); e.Handled = true; return; }
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && Model?.IsSaving != true) { Close(); e.Handled = true; }
    }
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
    private void OnKeepEditingClick(object? sender, RoutedEventArgs e) { if (Model is { } model) model.ConfirmDiscard = false; }
    private void OnDiscardClick(object? sender, RoutedEventArgs e) { _discard = true; Close(); }
    private void OnBeginAddRateClick(object? sender, RoutedEventArgs e)
    { Model?.BeginAddRateCommand.Execute(null); if (Model?.ShowAddRate == true) NewThicknessInput.Focus(); }
    private void OnStageRateClick(object? sender, RoutedEventArgs e) => StageRateAndScroll();
    private void StageRateAndScroll()
    {
        Model?.StageRateCommand.Execute(null);
        if (Model is { ShowAddRate: false, SelectedRate: { } row }) PriceGrid.ScrollIntoView(row, PriceGrid.Columns[3]);
    }
    private void OnBackFromThicknessConfirmationClick(object? sender, RoutedEventArgs e)
    { Model?.DismissThicknessConfirmation(); SavePricesButton.Focus(); }
    private void OnRemoveNewRateClick(object? sender, RoutedEventArgs e)
    { if (sender is Control { DataContext: PriceRowViewModel row }) Model?.RemoveNewRate(row); }
    private async void OnNewMachineClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || model.CreateMachineForm() is not { } form) return;
        try
        {
            var window = new NewMachineWindow { DataContext = form, RequestedThemeVariant = ActualThemeVariant };
            if (await window.ShowDialog<CuttingMachine?>(this) is { } machine) await model.ShowCreatedMachineAsync(machine);
        }
        catch (Exception exception) { model.Error = AppDiagnostics.Describe(DiagnosticOperation.AccessHost, exception, "Не удалось открыть добавление станка."); }
    }
    private async void OnMachineImportClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || model.CreateMachineImportForm() is not { } form) return;
        try
        {
            var window = new MachineImportWindow { DataContext = form, RequestedThemeVariant = ActualThemeVariant };
            if (await window.ShowDialog<CuttingMachine?>(this) is { } machine) await model.ShowCreatedMachineAsync(machine);
        }
        catch (Exception exception) { model.Error = AppDiagnostics.Describe(DiagnosticOperation.AccessHost, exception, "Не удалось открыть импорт спецификаций."); }
    }
}
