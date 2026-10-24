namespace Jornal.Desktop.Customization;

public sealed record EntrySuggestions
{
    public const int MaximumEntries = 200;
    public const int MaximumCharacters = 16000;
    public string[] Managers { get; init; } = [];
    public string[] Customers { get; init; } = [];
    public string[] Materials { get; init; } = [];
    public EntrySuggestions Normalize()
    {
        static string[] List(string[]? entries)
        {
            var result = (entries ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (result.Length > MaximumEntries) throw new ArgumentException($"Не больше {MaximumEntries} значений в каждом списке.");
            if (result.Any(value => value.Length > 255 || value.Any(char.IsControl))) throw new ArgumentException("Каждое значение — одна строка до 255 символов.");
            return result;
        }
        var managers = List(Managers); var customers = List(Customers); var materials = List(Materials);
        if (managers.Sum(value => value.Length) + customers.Sum(value => value.Length) + materials.Sum(value => value.Length) > MaximumCharacters)
            throw new ArgumentException("Списки слишком большие.");
        return new() { Managers = managers, Customers = customers, Materials = materials };
    }
    public static EntrySuggestions FromText(string? managers, string? customers, string? materials = "") => new EntrySuggestions
    {
        Managers = Lines(managers), Customers = Lines(customers), Materials = Lines(materials)
    }.Normalize();
    private static string[] Lines(string? value) => (value ?? "").Replace("\r\n", "\n").Split('\n');
}
