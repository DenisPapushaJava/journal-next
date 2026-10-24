namespace Jornal.Core.Pricing;

public static class PriceTables
{
    public const string Laser = "PriceLL500";
    public const string Plasma = "PriceP";
    public const string GasOxygen = "PriceG";
    public const string HansLaserId1 = "PriceHL_id1";
    public const string HansLaserId2 = "PriceHL_id2";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
        { "PriceLM500", Laser, Plasma, GasOxygen, HansLaserId1, HansLaserId2 });

    public static void ValidateName(string name)
    {
        if (!All.Contains(name, StringComparer.Ordinal) && name != PriceCatalog.UnusedHansLaser && !CuttingMachine.IsCustomTable(name))
            throw new ArgumentException("Неизвестная таблица прайса.", nameof(name));
    }
}

public sealed record PriceRate(decimal ThicknessMillimeters, string ColumnName, int? RublesPerMeter);

// Первая строка исходной таблицы. NULL тарифа отличается от настоящего нуля.
public sealed record PriceTable(string Name, bool HasRow, IReadOnlyList<PriceRate> Rates)
{
    public void Validate()
    {
        PriceTables.ValidateName(Name);
        if (Rates is null || Rates.Any(rate => rate is null || rate.ThicknessMillimeters <= 0 ||
                string.IsNullOrWhiteSpace(rate.ColumnName) || (!HasRow && rate.RublesPerMeter is not null)) ||
            Rates.Select(rate => rate.ThicknessMillimeters).Distinct().Count() != Rates.Count ||
            Rates.Select(rate => rate.ColumnName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Rates.Count)
            throw new ArgumentException("Некорректные или неоднозначные данные прайса.");
    }
}

public interface IPriceReader
{
    Task<PriceTable> ReadAsync(string tableName, CancellationToken cancellationToken = default);
}

public sealed class PriceReadException(string message, Exception? innerException = null) : Exception(message, innerException);
