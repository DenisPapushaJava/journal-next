using Jornal.Infrastructure.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Controls;

public partial class WindowsSecurityHelp : UserControl
{
    public const string SettingsUri = "ms-settings:windowsdefender";
    public const string ManualInstructions = "Не удалось открыть параметры. Откройте через «Пуск»: «Безопасность Windows» → «Управление приложениями/браузером». На рабочем компьютере обратитесь к ИТ.";

    public WindowsSecurityHelp() => InitializeComponent();

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AccessHostHelpViewModel model) return;
        model.OpenError = await OpenSettingsAsync(uri => TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri) ?? Task.FromResult(false));
    }

    public static async Task<string?> OpenSettingsAsync(Func<Uri, Task<bool>> launch)
    {
        try { return await launch(new Uri(SettingsUri)) ? null : ManualInstructions; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AppDiagnostics.Capture(DiagnosticOperation.Preferences, exception);
            return ManualInstructions;
        }
    }
}
