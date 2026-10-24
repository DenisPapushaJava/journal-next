using Avalonia.Interactivity;
using Jornal.Desktop.Customization;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.Views;

public partial class MainWindow
{
    private async void OnSetCustomColorClick(object? sender, RoutedEventArgs e) => await ShowRowColorAsync();
    private async Task ShowRowColorAsync()
    {
        if (_viewModel is not { } model || OwnedWindows.Count > 0) return;
        try
        {
            if (model.CreateRowColorForm() is { } form)
                await new RowColorWindow { DataContext = form, RequestedThemeVariant = ActualThemeVariant }.ShowDialog(this);
        }
        catch (Exception exception) { model.ErrorMessage = AppDiagnostics.Describe(DiagnosticOperation.OrderBatch, exception, "Не удалось открыть выбор цвета."); }
    }
    public string? SaveEntrySuggestions(EntrySuggestions value)
    {
        if (_viewModel is not { } model) return "Настройки недоступны.";
        var previous = model.CaptureEntrySuggestions();
        model.ApplyEntrySuggestions(value);
        if (_customization.Flush()) return null;
        var error = model.PreferencesError ?? "Не удалось сохранить списки.";
        model.ApplyEntrySuggestions(previous);
        return error;
    }
}
