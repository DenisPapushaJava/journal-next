using Avalonia.Controls;
using Avalonia.Interactivity;
using Jornal.Desktop.Updates;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Controls.Settings;

public partial class AppUpdatesPanel : UserControl
{
    public AppUpdatesPanel() => InitializeComponent();
    private async void OnOpenReleasesClick(object? sender, RoutedEventArgs e)
    {
        try { await TopLevel.GetTopLevel(this)!.Launcher.LaunchUriAsync(new Uri(VelopackAppUpdater.ReleasesUrl)); }
        catch (Exception) { if (DataContext is AppUpdatesViewModel model) model.Error = "Не удалось открыть браузер. Выпуски доступны в репозитории journal-v2.0 на GitHub."; }
    }
}
