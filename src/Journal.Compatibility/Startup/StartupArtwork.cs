namespace Jornal.Desktop.Startup;

// Оформление выбирается разработчиками и попадает к пользователю вместе с выпуском.
public sealed record StartupArtwork(Uri Background)
{
    public static StartupArtwork Current { get; } = new(new Uri("avares://Jornal.Desktop/Assets/Startup/laser-cutting.jpg"));
    public static Uri CompanyLogo { get; } = new("avares://Jornal.Desktop/Assets/Startup/company-logo.png");
}
