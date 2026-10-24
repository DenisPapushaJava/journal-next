using System.Globalization;
using Jornal.Core.Pricing;

namespace Jornal.Parsing.Specifications;

public sealed record NormalizedHansSpecification(DateOnly OrderDate, string SpecificationNumber, string CuttingType,
    string Material, string MaterialId, string CuttingGas, decimal ThicknessMillimeters, string SheetSize, string SheetCount,
    int CutLengthMeters, string WeightKilograms, int PartCount, int PunchCount, decimal Coefficient);

public static class HansSpecificationNormalizer
{
    public static NormalizedHansSpecification Normalize(SpecificationParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        if (parsed.Profile != HansPdfSpecificationParser.Profile || !parsed.IsComplete)
            throw new ArgumentException("Спецификация HansLaser неполна или содержит неоднозначные поля.", nameof(parsed));
        var sizes = parsed.SheetLayouts.Select(layout => layout.Value.Dimensions).Distinct().ToArray();
        return new(parsed.Header!.Value.OrderDate, parsed.Header.Value.JournalNumber, parsed.CuttingType!.Value,
            parsed.Material!.Value, parsed.MaterialId!.Value, parsed.CuttingGas!.Value, sizes[0].ThicknessMillimeters,
            BlockSpecificationNormalizer.SheetSizes(sizes), parsed.SheetCount!.Value.ToString(CultureInfo.InvariantCulture),
            LegacyCutLengthMeters(parsed.CutLengthMillimeters!.Value), parsed.WeightKilograms!.Value.ToString("0.############################", CultureInfo.InvariantCulture),
            parsed.PartCount!.Value, parsed.PunchCount!.Value, CuttingPriceCalculator.CalculatePunchCoefficient(parsed.PartCount.Value, parsed.PunchCount.Value));
    }

    public static int LegacyCutLengthMeters(decimal millimeters)
    {
        // Старая HansLaser-ветка использует Double и Ceiling; правило БЛОК +1 здесь неприменимо.
        var meters = Math.Ceiling((double)millimeters / 1000);
        if (millimeters < 0 || !double.IsFinite(meters) || meters > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(millimeters));
        return (int)meters;
    }
}
