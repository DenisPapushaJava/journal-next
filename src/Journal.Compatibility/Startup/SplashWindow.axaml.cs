using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Jornal.Desktop.Startup;

public partial class SplashWindow : Window
{
    private readonly Bitmap? _artwork;
    private readonly Bitmap? _logo;

    public SplashWindow() : this(StartupArtwork.Current) { }

    public SplashWindow(StartupArtwork artwork)
    {
        InitializeComponent();
        _artwork = LoadOptionalBitmap(artwork.Background);
        _logo = LoadOptionalBitmap(StartupArtwork.CompanyLogo);
        ArtworkImage.Source = _artwork;
        CompanyLogoImage.Source = _logo;
        LogoFallback.IsVisible = _logo is null;
        var version = typeof(SplashWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? typeof(SplashWindow).Assembly.GetName().Version?.ToString() ?? "—";
        VersionLabel.Text = $"Версия {version}";
    }

    public void ShowStartupError()
    {
        Title = "Journal — ошибка запуска";
        LaunchStatus.IsVisible = false;
        ApplicationName.IsVisible = false;
        StartupErrorMessage.Text = "Закройте приложение и попробуйте запустить снова. Если ошибка повторяется, обратитесь к сопровождающему Journal.";
        StartupErrorPanel.IsVisible = true;
        CloseSplashButton.Focus();
    }

    public void ShowLoadingOrders() => LaunchStatus.Text = "Загружаем заявки…";
    public void ShowOpeningJournal() => LaunchStatus.Text = "Открываем журнал…";

    private static Bitmap? LoadOptionalBitmap(Uri resource)
    {
        try
        {
            using var stream = AssetLoader.Open(resource);
            return new Bitmap(stream);
        }
        catch (Exception exception)
        {
            // Декоративная картинка не должна препятствовать работе с журналом.
            AppDiagnostics.Capture(DiagnosticOperation.Startup, exception);
            return null;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    protected override void OnClosed(EventArgs e)
    {
        ArtworkImage.Source = null;
        CompanyLogoImage.Source = null;
        _artwork?.Dispose();
        _logo?.Dispose();
        base.OnClosed(e);
    }
}
