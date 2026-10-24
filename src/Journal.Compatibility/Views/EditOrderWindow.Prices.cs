using Avalonia.Controls;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.Views;

public partial class EditOrderWindow
{
    private async void OnThicknessPricesRequested(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } model || e.Source is not Control { DataContext: OrderEntryField field } ||
            model.CreateThicknessPriceEditor(field) is not { } form) return;
        e.Handled = true; model.SetPriceEditorOpen(true);
        try { await new PriceEditorWindow { DataContext = form, RequestedThemeVariant = ActualThemeVariant }.ShowDialog(this); }
        catch (Exception exception)
        { model.Error = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось открыть прайсы."); }
        finally
        {
            model.SetPriceEditorOpen(false);
            try
            { if (!_importLifetime.IsCancellationRequested) await model.RefreshAfterPriceEditingAsync(_importLifetime.Token); }
            catch (OperationCanceledException) when (_importLifetime.IsCancellationRequested) { }
            catch (Exception exception)
            { model.Error = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось обновить толщины."); }
        }
    }
}
