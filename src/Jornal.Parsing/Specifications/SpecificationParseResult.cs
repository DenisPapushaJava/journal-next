namespace Jornal.Parsing.Specifications;

public enum SpecificationField
{
    Header, CuttingType, Material, Dimensions, SheetCount, CutLength, Weight, PartCount, PunchCount, CuttingDuration,
    MaterialId, CuttingGas
}

public enum SpecificationIssueSeverity { Warning, Error }

public sealed record SpecificationIssue(SpecificationField Field, string Code, string Message,
    SpecificationIssueSeverity Severity = SpecificationIssueSeverity.Error, IReadOnlyList<SpecificationValueSource>? Sources = null);

[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "Kind")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(HtmlValueSource), "Html")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(PdfValueSource), "Pdf")]
public abstract record SpecificationValueSource(string Label, string RawText);

// Индексы таблицы, строки и ячейки — с нуля, как в SourceDocument; значение не зависит от имени внешнего файла.
public sealed record HtmlValueSource(int Table, int Row, int Cell, string Label, string RawText) : SpecificationValueSource(Label, RawText);
public sealed record PdfValueSource(int Page, double Left, double Bottom, double Right, double Top, string Label, string RawText)
    : SpecificationValueSource(Label, RawText);

public sealed record SpecificationValue<T>(T Value, IReadOnlyList<SpecificationValueSource> Sources);

public sealed record SpecificationHeader(DateOnly OrderDate, string OriginalIdentifier, string JournalNumber, string OrderName);
public sealed record SheetDimensions(decimal ThicknessMillimeters, decimal LengthMillimeters, decimal WidthMillimeters);
public sealed record SheetLayout(SheetDimensions Dimensions, int? SheetCount);

public sealed record SpecificationParseResult(
    string Profile,
    SpecificationValue<SpecificationHeader>? Header,
    SpecificationValue<string>? CuttingType,
    SpecificationValue<string>? Material,
    SpecificationValue<SheetDimensions>? Dimensions,
    SpecificationValue<int>? SheetCount,
    SpecificationValue<decimal>? CutLengthMillimeters,
    SpecificationValue<decimal>? WeightKilograms,
    SpecificationValue<int>? PartCount,
    SpecificationValue<int>? PunchCount,
    SpecificationValue<TimeSpan>? CuttingDuration,
    IReadOnlyList<SpecificationIssue> Issues)
{
    // Структура известного отчёта найдена. Полнота/конфликты полей проверяются независимо через IsComplete/Issues.
    public bool IsRecognized { get; init; }
    public IReadOnlyList<SpecificationValue<SheetLayout>> SheetLayouts { get; init; } = [];
    public SpecificationValue<string>? MaterialId { get; init; }
    public SpecificationValue<string>? CuttingGas { get; init; }
    public bool HasErrors => Issues.Any(issue => issue.Severity == SpecificationIssueSeverity.Error);
    public bool IsComplete => IsRecognized && !HasErrors && Header is not null && CuttingType is not null && Material is not null &&
        SheetLayouts.Count > 0 && SheetLayouts.Select(layout => layout.Value.Dimensions.ThicknessMillimeters).Distinct().Count() == 1 &&
        SheetCount is not null && CutLengthMillimeters is not null && WeightKilograms is not null &&
        (Profile == WaterjetPdfSpecificationParser.Profile || PartCount is not null && PunchCount is not null) &&
        (Profile != HansPdfSpecificationParser.Profile || MaterialId is not null && CuttingGas is not null);
}
