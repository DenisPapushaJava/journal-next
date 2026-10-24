using System.Globalization;
using System.Text.Json.Serialization;
using Jornal.Core.Pricing;

namespace Jornal.Core.Orders;

// Ручной ввод до расчёта. Текстовые поля Access сохраняют своё представление.
public sealed record ManualOrderDraft(
    DateOnly OrderDate,
    string SpecificationNumber,
    string ManagerName,
    string CustomerName,
    string CuttingType,
    string MaterialName,
    decimal ThicknessMillimeters,
    string SheetSize,
    string SheetCount,
    int CutLengthMeters,
    string WeightKilograms,
    decimal Coefficient,
    string MaterialId,
    string CuttingGas)
{
    public CuttingMachine? Machine { get; init; }
    public long? CuttingDurationMinutes { get; init; }
    public bool HasBending { get; init; }
    public string Comment { get; init; } = "";
    public int InitialColor { get; init; } = OrderColors.None;
    public int? PartCount { get; init; }
    public int? PunchCount { get; init; }
    public CuttingPriceQuote? ExpectedQuote { get; init; }
    [JsonIgnore] public CuttingPriceRequest PriceRequest => new(CuttingType, ThicknessMillimeters, CutLengthMeters, Coefficient, MaterialId);
    [JsonIgnore] public string StoredCoefficient => Coefficient.ToString(CultureInfo.InvariantCulture) + "|" +
        (PartCount?.ToString(CultureInfo.InvariantCulture) ?? "-1") + "|" + (PunchCount?.ToString(CultureInfo.InvariantCulture) ?? "-1") + "|" +
        (string.IsNullOrEmpty(MaterialId) ? "-1" : MaterialId) + "|" + (string.IsNullOrEmpty(CuttingGas) ? "-1" : CuttingGas);

    public void Validate()
    {
        OrderAnnotations.ValidateComment(Comment);
        if (CuttingDurationMinutes is < 0 || CuttingDurationMinutes > OrderCuttingTime.MaximumMinutes)
            throw new OrderWriteException(OrderWriteFailure.NotSaved, CuttingDuration.ValidationMessage);
        var required = new[] { SpecificationNumber, ManagerName, CustomerName, MaterialName, SheetSize, SheetCount, WeightKilograms };
        if (required.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 255 || value.Any(char.IsControl)) ||
            OrderDate.Year < 100 || Machine?.SpecificationProfileId != CuttingMachine.WaterjetProfile && MaterialName.Any(char.IsWhiteSpace) || MaterialId is null || CuttingGas is null ||
            MaterialId.Length > 255 || CuttingGas.Length > 255 ||
            MaterialId.Any(c => char.IsControl(c) || c == '|') || CuttingGas.Any(c => char.IsControl(c) || c == '|') ||
            StoredCoefficient.Length > 255 || PartCount is < 1 || PunchCount is < 0)
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Проверьте обязательные поля, дату и формат ID/газа. Текстовые поля MDB — не более 255 символов.");
        if (!OrderSheetCount.IsValid(SheetCount))
            throw new OrderWriteException(OrderWriteFailure.NotSaved, OrderSheetCount.ValidationMessage);
        _ = CuttingPriceCalculator.SelectTable(PriceRequest, Machine);
    }

    public CuttingPriceQuote Calculate(PriceTable table, CuttingMachine? machine = null)
    {
        var quote = CuttingPriceCalculator.Calculate(PriceRequest, table, machine ?? Machine);
        if (ExpectedQuote is not null && ExpectedQuote != quote) throw new OrderPriceChangedException(quote);
        return quote;
    }

    public OrderSummary ToOrder(int id, decimal? price)
    {
        var row = new OrderSummary(id, OrderDate, SpecificationNumber, ManagerName,
        CustomerName, CuttingType, MaterialName, ThicknessMillimeters, price, InitialColor)
    {
        SheetSize = SheetSize, SheetCount = SheetCount, CutLengthMeters = CutLengthMeters, WeightKilograms = WeightKilograms,
        Coefficient = Coefficient, PartCount = PartCount, PunchCount = PunchCount,
        MaterialId = MaterialId == "-1" ? "" : MaterialId, CuttingGas = CuttingGas == "-1" ? "" : CuttingGas,
        StoredOrderDateTime = OrderDate.ToDateTime(TimeOnly.MinValue), StoredCoefficient = StoredCoefficient
        };
        return row with { CuttingTime = CuttingDurationMinutes is { } minutes ? OrderCuttingTime.For(row, minutes) : null,
            Annotations = OrderAnnotations.For(row, HasBending, Comment) };
    }
}

public sealed class OrderPriceChangedException(CuttingPriceQuote quote) : Exception(
    "Прайс изменился после предварительного расчёта. Заявка не добавлена. Проверьте новую цену и нажмите «Добавить заявку» ещё раз.")
{
    public CuttingPriceQuote Quote { get; } = quote;
}
