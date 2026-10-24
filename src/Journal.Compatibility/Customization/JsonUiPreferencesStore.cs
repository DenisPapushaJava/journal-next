using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using System.Text.Json;

namespace Jornal.Desktop.Customization;

public sealed class JsonUiPreferencesStore(string path) : IUiPreferencesStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(
        OperatingSystem.IsWindows() ? Environment.SpecialFolder.LocalApplicationData :
            Environment.SpecialFolder.ApplicationData), "Journal", "ui-settings.json");

    public PreferencesLoadResult Load()
    {
        try
        {
            if (!File.Exists(path)) return new(null);
            if (new FileInfo(path).Length > 128 * 1024)
                return new(null, "Файл настроек экрана слишком большой. Используется стандартный вид.");
            UiPreferences? preferences;
            using (var stream = File.OpenRead(path)) preferences = JsonSerializer.Deserialize<UiPreferences>(stream, Options);
            if (preferences is null || preferences.Version != 1 || preferences.Layout is null)
                return new(null, "Настройки экрана несовместимы с этой версией. Используется стандартный вид.");
            EntrySuggestions suggestions; string? error = null;
            try { suggestions = (preferences.EntrySuggestions ?? EntrySuggestionDefaults.Create()).Normalize(); }
            catch (ArgumentException) { suggestions = new(); error = "Сохранённые списки повреждены. Используются пустые списки."; }
            var normalized = preferences with { Layout = preferences.Layout.Normalize(), ResultFormatting = (preferences.ResultFormatting ?? new()).Normalize(),
                ResultPricing = (preferences.ResultPricing ?? new()).Normalize(), NewOrderColor = (preferences.NewOrderColor ?? new()).Normalize(), EntrySuggestions = suggestions };
            return error is not null ? new(normalized, error) : CompactPanelsMigration.Apply(normalized, Save);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            AppDiagnostics.Capture(DiagnosticOperation.Preferences, exception);
            return new(null, "Не удалось прочитать настройки экрана. Используется стандартный вид.");
        }
    }

    public void Save(UiPreferences preferences)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".ui-settings-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, preferences with { Layout = preferences.Layout.Normalize() with { CompactPanelsPresetApplied = true },
                    ResultFormatting = (preferences.ResultFormatting ?? new()).Normalize(),
                    ResultPricing = (preferences.ResultPricing ?? new()).Normalize(), NewOrderColor = (preferences.NewOrderColor ?? new()).Normalize(),
                    EntrySuggestions = (preferences.EntrySuggestions ?? EntrySuggestionDefaults.Create()).Normalize() }, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
