using System.Globalization;
using System.Text.Json.Serialization;
using Jornal.Core.Pricing;

namespace Jornal.Core.Orders;

// Полные значения 16 полей Orders. В отличие от модели таблицы, NULL и Double не нормализуются.
public sealed record OrderRecord(int Id, DateTime? Date, string? Manager, string? Specification,
    string? Customer, string? CuttingType, string? Material, string? SheetSize, string? SheetCount,
    decimal? Price, double? Thickness, int? CutLength, string? Weight, string? DeliveryNumber,
    int? Color, string? Coefficient)
{
    public OrderCuttingTime? CuttingTime { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public OrderAnnotations? Annotations { get; init; }
    public void Validate()
    {
        CuttingTime?.Validate();
        Annotations?.Validate();
        if (Id <= 0 || Date is { Year: < 100 } || Thickness is { } thickness && !double.IsFinite(thickness) ||
            Price is { } price && (price < -922337203685477.5808m || price > 922337203685477.5807m || decimal.Round(price, 4) != price) ||
            TextValues.Any(text => text?.Length > 255))
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Запись не соответствует прежним типам Orders. Данные не изменены.");
    }

    [JsonIgnore] public string?[] TextValues => [Manager, Specification, Customer, CuttingType, Material,
        SheetSize, SheetCount, Weight, DeliveryNumber, Coefficient];

    public OrderSummary ToSummary()
    {
        var parts = Coefficient?.Split('|', StringSplitOptions.TrimEntries) ?? [];
        decimal? coefficient = parts.Length > 0 && decimal.TryParse(parts[0].Replace(',', '.'),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) && parsed != -1 ? parsed : null;
        int? Count(int index) => parts.Length > index && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : null;
        return new(Id, Date is { } date ? DateOnly.FromDateTime(date) : null, Specification ?? "", Manager ?? "",
            Customer ?? "", CuttingType ?? "", Material ?? "", Thickness is { } t ? Convert.ToDecimal(t) : null, Price, Color)
        {
            CuttingTime = CuttingTime, Annotations = Annotations, SheetSize = SheetSize ?? "", SheetCount = SheetCount ?? "", CutLengthMeters = CutLength, WeightKilograms = Weight ?? "",
            DeliveryNumber = DeliveryNumber, StoredOrderDateTime = Date, StoredCoefficient = Coefficient,
            Coefficient = coefficient, PartCount = Count(1), PunchCount = Count(2),
            MaterialId = parts.Length > 3 && parts[3] != "-1" ? parts[3] : "",
            CuttingGas = parts.Length > 4 && parts[4] != "-1" ? parts[4] : ""
        };
    }

    // Только для деморежима/старых Linux-копий: исходная модель просмотра уже нормализована.
    public static OrderRecord FromSummary(OrderSummary row) => new(row.Id, row.StoredOrderDateTime ?? row.OrderDate?.ToDateTime(TimeOnly.MinValue),
        row.ManagerName, row.SpecificationNumber, row.CustomerName, row.CuttingType, row.MaterialName, row.SheetSize,
        row.SheetCount, row.StoredCuttingPrice, row.ThicknessMillimeters is { } t ? (double)t : null, row.CutLengthMeters,
        row.WeightKilograms, row.DeliveryNumber, row.Color, row.StoredCoefficient ?? row.Coefficient?.ToString(CultureInfo.InvariantCulture))
        { CuttingTime = row.CuttingTime, Annotations = row.Annotations };
}

public sealed record OrderEditChange(OrderRecord Expected, OrderRecord Values, CuttingPriceQuote? ExpectedQuote = null)
{
    public CuttingMachine? Machine { get; init; }
    [JsonIgnore] public bool PriceParametersChanged
    {
        get
        {
            var before = Expected.ToSummary(); var after = Values.ToSummary();
            return before.CuttingType != after.CuttingType || before.ThicknessMillimeters != after.ThicknessMillimeters ||
                before.CutLengthMeters != after.CutLengthMeters || before.Coefficient != after.Coefficient ||
                after.CuttingType == "HansLaser" && before.MaterialId != after.MaterialId;
        }
    }
    [JsonIgnore] public bool RequiresPriceCalculation => PriceParametersChanged || ExpectedQuote is not null;
    public void Validate()
    {
        Expected.Validate(); Values.Validate();
        if (Expected.Id != Values.Id || Expected.Color != Values.Color || Expected.DeliveryNumber != Values.DeliveryNumber || Expected.Price != Values.Price)
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Редактирование не должно менять код, цвет, БЛЦБ или цену вручную.");
        var before = Expected.TextValues; var after = Values.TextValues;
        if (Enumerable.Range(0, 8).Any(index => before[index] != after[index] &&
                (string.IsNullOrWhiteSpace(after[index]) || after[index]!.Any(char.IsControl))) ||
            Values.Material != Expected.Material && Machine?.SpecificationProfileId != CuttingMachine.WaterjetProfile && Values.Material?.Any(char.IsWhiteSpace) == true ||
            Values.Date != Expected.Date && Values.Date is null)
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Проверьте изменённые поля. Обязательный текст нельзя очистить, материал указывается без пробелов.");
        if (Values.SheetCount != Expected.SheetCount && !OrderSheetCount.IsValid(Values.SheetCount))
            throw new OrderWriteException(OrderWriteFailure.NotSaved, OrderSheetCount.ValidationMessage);
        _ = CuttingPriceCalculator.SelectTable(PriceRequest, Machine);
    }

    [JsonIgnore] public CuttingPriceRequest PriceRequest
    {
        get
        {
            var row = Values.ToSummary();
            if (row.ThicknessMillimeters is not { } thickness || row.CutLengthMeters is not { } length || row.Coefficient is not { } coefficient)
                throw new PriceCalculationException(PriceCalculationFailure.InvalidParameters,
                    "Для сохранения с расчётом укажите толщину, длину и коэффициент. Неизвестные значения не заменяются нулём или единицей.");
            return new(row.CuttingType, thickness, length, coefficient, row.MaterialId);
        }
    }

    public OrderRecord Calculate(PriceTable? table, CuttingMachine? machine = null)
    {
        Validate();
        if (!RequiresPriceCalculation) return Values;
        ArgumentNullException.ThrowIfNull(table);
        var quote = CuttingPriceCalculator.Calculate(PriceRequest, table, machine ?? Machine);
        if (ExpectedQuote is not null && ExpectedQuote != quote) throw new OrderEditPriceChangedException(quote);
        return Values with { Price = quote.StoredPrice };
    }
}

public sealed class OrderEditPriceChangedException(CuttingPriceQuote quote) : Exception(
    "Прайс изменился. Правки не записаны. Проверьте новую цену и нажмите «Сохранить» ещё раз.")
{
    public CuttingPriceQuote Quote { get; } = quote;
}

public interface IOrderMaintenance : IPriceReader
{
    Task<OrderRecord?> ReadRecordAsync(int id, CancellationToken cancellationToken = default);
    Task<OrderRecord> UpdateAsync(OrderEditChange change, CancellationToken cancellationToken = default);
    Task DeleteAsync(OrderRecord expected, CancellationToken cancellationToken = default);
    Task<OrderRecord> RestoreAsync(OrderRecord archived, CancellationToken cancellationToken = default);
}
