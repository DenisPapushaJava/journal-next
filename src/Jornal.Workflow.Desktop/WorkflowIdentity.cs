using System.Reflection;
using System.Runtime.InteropServices;

namespace Jornal.Workflow.Desktop;

public static class WorkflowIdentity
{
    public const string PackageId = "DenisPapusha.JournalWorkflow";
    // Display branding is independent of the stable package/profile/update identity.
    public const string Name = "Заявки";
    public const string WindowTitle = Name;
    public const string SettingsTitle = "Настройки · " + Name;
    public const string UpdatesTitle = "Обновления · " + Name;
    public const string TrashTitle = "Корзина заявок · " + Name;
    public const string ShopTransferTitle = "Задание в цех · " + Name;
    public const string SettingsFolder = "JournalWorkflow";
    public const string ChannelPrefix = "workflow";
    public const string TagPrefix = "workflow-v";
    // Отдельный проверенный репозиторий будет задан при подготовке первого выпуска.
    public static Uri? UpdatesRepository => new("https://github.com/DenisPapushaJava/journal-next");
    public static string Channel => ChannelPrefix + "-" + Jornal.Desktop.Updates.VelopackAppUpdater.RuntimeChannel;
    public static string Version => typeof(WorkflowIdentity).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "разработка";
    public static string ProfileDirectory => Path.Combine(Environment.GetFolderPath(OperatingSystem.IsWindows()
        ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.ApplicationData), SettingsFolder);
    public static string WorkspacePath => Path.Combine(ProfileDirectory, "development-workspace.json");
    public static string PreferencesPath => Path.Combine(ProfileDirectory, "preferences.json");
    public static string UpdatePreferencesPath => Path.Combine(ProfileDirectory, "update-settings.json");
    public static string DiagnosticsDirectory => Path.Combine(ProfileDirectory, "Diagnostics");

    public static Uri ReleasesPageFor(Uri repository)
    {
        ValidateRepository(repository);
        var path = Uri.UnescapeDataString(repository.AbsolutePath).Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return new Uri("https://github.com/" + path + "/releases");
    }

    public static void ValidateRepository(Uri repository)
    {
        var path = Uri.UnescapeDataString(repository.AbsolutePath).Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        if (repository.Scheme != "https" || repository.Host != "github.com" || !repository.IsDefaultPort || repository.UserInfo.Length != 0 ||
            repository.Query.Length != 0 || repository.Fragment.Length != 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$") ||
            path.Equals("DenisPapushaJava/journal-v2.0", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Новому Journal нужен отдельный HTTPS-репозиторий выпусков на GitHub.", nameof(repository));
    }
}
