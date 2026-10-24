using System.Text.Json;

namespace Jornal.Desktop.Updates;

public sealed record AppUpdatePreferences(int Version = 1, bool IncludePreview = false, bool CheckAutomatically = true);
public sealed record AppUpdatePreferencesLoad(AppUpdatePreferences? Preferences, string? Error = null);

public interface IAppUpdatePreferencesStore
{
    AppUpdatePreferencesLoad Load();
    void Save(AppUpdatePreferences preferences);
}

public sealed class JsonAppUpdatePreferencesStore(string path) : IAppUpdatePreferencesStore
{
    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(Customization.JsonUiPreferencesStore.DefaultPath)!, "update-settings.json");

    public AppUpdatePreferencesLoad Load()
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 16 * 1024) return InvalidSettings();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
                !root.TryGetProperty("IncludePreview", out var preview) || preview.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return InvalidSettings();
            var automatic = true; // Совместимость с профилем alpha.2, где автопроверка была включена.
            if (root.TryGetProperty("CheckAutomatically", out var check))
            {
                if (check.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return InvalidSettings();
                automatic = check.GetBoolean();
            }
            return new(new(IncludePreview: preview.GetBoolean(), CheckAutomatically: automatic));
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new(null, "Не удалось прочитать настройки обновлений. Автопроверка отключена, выбран стабильный канал. Укажите настройки заново.");
        }
    }

    private static AppUpdatePreferencesLoad InvalidSettings() =>
        new(null, "Настройки обновлений несовместимы с этой версией. Автопроверка отключена, выбран стабильный канал. Укажите настройки заново.");

    public void Save(AppUpdatePreferences preferences)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".update-settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, preferences, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
