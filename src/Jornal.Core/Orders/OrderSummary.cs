namespace Jornal.Core.Orders;

// A read model, not the complete persisted order or a recalculated quote.
public sealed record OrderSummary(
    int Id,
    DateOnly? OrderDate,
    string SpecificationNumber,
    string ManagerName,
    string CustomerName,
    string CuttingType,
    string MaterialName,
    decimal? ThicknessMillimeters,
    decimal? StoredCuttingPrice,
    int? Color)
{
    public OrderCuttingTime? CuttingTime { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public OrderAnnotations? Annotations { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool AnnotationsUnavailable { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public bool AnnotationsNeedReview => Annotations is not null && !Annotations.Matches(this);
    [System.Text.Json.Serialization.JsonIgnore] public bool HasBending => !AnnotationsUnavailable && !AnnotationsNeedReview && Annotations?.HasBending == true;
    [System.Text.Json.Serialization.JsonIgnore] public string Comment => !AnnotationsUnavailable && !AnnotationsNeedReview ? Annotations?.Comment ?? "" : "";
    [System.Text.Json.Serialization.JsonIgnore] public long? CuttingDurationMinutes =>
        CuttingTime is { Minutes: >= 0 } time && time.Minutes <= OrderCuttingTime.MaximumMinutes && time.Matches(this) ? time.Minutes : null;
    [System.Text.Json.Serialization.JsonIgnore] public bool CuttingTimeNeedsReview => CuttingTime is not null && CuttingDurationMinutes is null;
    public string SheetSize { get; init; } = string.Empty;
    public string SheetCount { get; init; } = string.Empty;
    public int? CutLengthMeters { get; init; }
    public string WeightKilograms { get; init; } = string.Empty;
    public string? DeliveryNumber { get; init; }
    public decimal? Coefficient { get; init; }
    public string MaterialId { get; init; } = string.Empty;
    public string CuttingGas { get; init; } = string.Empty;
    // Исходные значения нужны для диагностики и будущего редактирования без нормализации при чтении.
    public string? StoredCoefficient { get; init; }
    public DateTime? StoredOrderDateTime { get; init; }
    public int? PartCount { get; init; }
    public int? PunchCount { get; init; }
}
