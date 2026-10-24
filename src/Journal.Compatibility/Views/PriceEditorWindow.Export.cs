using Avalonia.Interactivity;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.Views;

public partial class PriceEditorWindow
{
    private async void OnExportPricesClick(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model || model.CreateExportForm() is not { } export) return;
        model.SetExportDialogOpen(true);
        try
        {
            if (await new PriceExportWindow { DataContext = export, RequestedThemeVariant = ActualThemeVariant }.ShowDialog<bool>(this))
                model.Status = "Прайсы экспортированы";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { model.Error = AppDiagnostics.Describe(DiagnosticOperation.Unexpected, exception, "Не удалось открыть экспорт прайсов."); }
        finally { model.SetExportDialogOpen(false); }
    }
}
