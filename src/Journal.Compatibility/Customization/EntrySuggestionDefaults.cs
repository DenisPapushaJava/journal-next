using System.Text.Json;

namespace Jornal.Desktop.Customization;

/// <summary>Стартовый набор выпуска. Сохранённые пользовательские списки имеют приоритет.</summary>
public static class EntrySuggestionDefaults
{
    private static readonly EntrySuggestions Defaults = Load();

    // Normalize возвращает отдельные массивы, чтобы изменения профиля не меняли набор выпуска.
    public static EntrySuggestions Create() => Defaults.Normalize();

    private static EntrySuggestions Load()
    {
        using var stream = typeof(EntrySuggestionDefaults).Assembly.GetManifestResourceStream(
            "Jornal.Desktop.Customization.Data.entry-suggestions.json")
            ?? throw new InvalidOperationException("Не найден стартовый набор выпадающих списков.");
        return (JsonSerializer.Deserialize<EntrySuggestions>(stream)
            ?? throw new InvalidOperationException("Не заполнен стартовый набор выпадающих списков.")).Normalize();
    }
}
