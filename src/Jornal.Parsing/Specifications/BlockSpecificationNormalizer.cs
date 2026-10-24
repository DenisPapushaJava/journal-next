using System.Globalization;
using Jornal.Core.Pricing;

namespace Jornal.Parsing.Specifications;

public sealed record NormalizedBlockSpecification(DateOnly OrderDate, string SpecificationNumber, string CuttingType,
    string Material, decimal ThicknessMillimeters, string SheetSize, string SheetCount, int CutLengthMeters,
    string WeightKilograms, int PartCount, int PunchCount, decimal Coefficient);

public static class BlockSpecificationNormalizer
{
    public static NormalizedBlockSpecification Normalize(SpecificationParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.Profile != BlockHtmlSpecificationParser.Profile || !parsed.IsComplete)
            throw new ArgumentException("Спецификация неполна или содержит неоднозначные поля. Проверьте сообщения разбора.", nameof(parsed));
        var dimensions = parsed.SheetLayouts.Select(layout => layout.Value.Dimensions).Distinct().ToArray();
        var header = parsed.Header!.Value;
        return new(header.OrderDate, header.JournalNumber, parsed.CuttingType!.Value, parsed.Material!.Value,
            dimensions[0].ThicknessMillimeters, SheetSizes(dimensions),
            Number(parsed.SheetCount!.Value), LegacyCutLengthMeters(parsed.CutLengthMillimeters!.Value),
            Number(parsed.WeightKilograms!.Value), parsed.PartCount!.Value, parsed.PunchCount!.Value,
            CuttingPriceCalculator.CalculatePunchCoefficient(parsed.PartCount.Value, parsed.PunchCount.Value));
    }

    internal static string SheetSizes(IEnumerable<SheetDimensions> dimensions) => string.Join(", ", dimensions
        .Select(size => Number(size.WidthMillimeters) + "x" + Number(size.LengthMillimeters)).Distinct(StringComparer.Ordinal));

    public static int LegacyCutLengthMeters(decimal millimeters)
    {
        if (millimeters < 0 || decimal.Truncate(millimeters) / 1000 >= int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(millimeters), "Длина реза выходит за допустимый диапазон.");
        // Совместимость БЛОК: это намеренно не Ceiling. Точное кратное 1000 тоже получает дополнительный метр.
        return checked((int)(decimal.Truncate(millimeters) / 1000) + 1);
    }

    private static string Number(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);
}
