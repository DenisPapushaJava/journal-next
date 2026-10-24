using System.Globalization;

namespace Jornal.Core.Pricing;

public sealed record CuttingPriceRequest(string CuttingType, decimal ThicknessMillimeters,
    int CutLengthMeters, decimal Coefficient, string? MaterialId = null);

public sealed record CuttingPriceQuote(string TableName, string ThicknessColumn, int RublesPerMeter,
    int CutLengthMeters, decimal Coefficient, decimal StoredPrice);

public enum PriceCalculationFailure
{
    InvalidParameters, UnsupportedCuttingType, ThicknessOutOfRange, PriceUnavailable, InvalidPrice, AmountOutOfRange
}

public sealed class PriceCalculationException(PriceCalculationFailure failure, string message) : Exception(message)
{
    public PriceCalculationFailure Failure { get; } = failure;
}

public static class CuttingPriceCalculator
{
    public static string SelectTable(CuttingPriceRequest request, CuttingMachine? machine = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ThicknessMillimeters <= 0 || request.CutLengthMeters < 0 || request.Coefficient <= 0)
            throw new PriceCalculationException(PriceCalculationFailure.InvalidParameters,
                "Толщина и коэффициент должны быть больше нуля, длина — неотрицательным целым числом метров.");
        return SelectTableForMachine(request.CuttingType, request.MaterialId, machine);
    }

    public static IReadOnlyList<decimal> AvailableThicknesses(string cuttingType, string? materialId, PriceTable table, CuttingMachine? machine = null)
    {
        var selected = SelectTableForMachine(cuttingType, materialId, machine);
        table.Validate();
        if (table.Name != selected) throw new ArgumentException("Получен прайс другого станка.");
        return !table.HasRow ? [] : table.Rates.Where(rate => rate.RublesPerMeter is >= 0)
            .Select(rate => rate.ThicknessMillimeters).Order().ToArray();
    }

    public static string SelectTableForMachine(string cuttingType, string? materialId, CuttingMachine? machine = null)
    {
        if (machine is not null && (machine.Name != cuttingType || CuttingMachine.LegacyNames.Contains(cuttingType)))
            throw new PriceCalculationException(PriceCalculationFailure.UnsupportedCuttingType, "Привязка станка не соответствует виду резки.");
        return cuttingType switch
        {
            "Лазер" => PriceTables.Laser,
            "Плазма" => PriceTables.Plasma,
            "Газо-кислородная" => PriceTables.GasOxygen,
            "HansLaser" => materialId switch
            {
                "1" => PriceTables.HansLaserId1, "2" => PriceTables.HansLaserId2, _ => PriceTables.Laser
            },
            _ when machine is not null && machine.Name == cuttingType => CustomTable(machine),
            _ => throw new PriceCalculationException(PriceCalculationFailure.UnsupportedCuttingType, "Неизвестный вид резки.")
        };
    }

    private static string CustomTable(CuttingMachine machine) { machine.Validate(); return machine.TableName; }

    public static CuttingPriceQuote Calculate(CuttingPriceRequest request, PriceTable table, CuttingMachine? machine = null)
    {
        var result = CalculateResult(request, table, machine);
        return result.Quote ?? throw new PriceCalculationException(result.Failure!.Value, result.Error!);
    }

    public static CuttingPriceResult CalculateResult(CuttingPriceRequest request, PriceTable table, CuttingMachine? machine = null)
    {
        string selected;
        try { selected = SelectTable(request, machine); }
        catch (PriceCalculationException exception) { return new(exception.Failure, exception.Message); }
        table.Validate();
        if (table.Name != selected)
            throw new ArgumentException("Передан прайс другого вида резки или ID материала.", nameof(table));
        var rate = table.Rates.SingleOrDefault(rate => rate.ThicknessMillimeters == request.ThicknessMillimeters);
        if (!table.HasRow || rate?.RublesPerMeter is null)
            return new(PriceCalculationFailure.PriceUnavailable,
                $"{request.CuttingType}: нет тарифа для {request.ThicknessMillimeters.ToString("0.############################", CultureInfo.GetCultureInfo("ru-RU"))} мм. Добавьте его в «Прайсы».");
        if (rate.RublesPerMeter < 0)
            return new(PriceCalculationFailure.InvalidPrice, "Тариф в базе отрицательный. Проверьте прайс.");
        // Совместимость с NewOrd/Edit: Double, прежний порядок умножения и ToEven.
        // Переход к decimal-арифметике изменил бы некоторые цены на границе округления.
        var rounded = Math.Round((double)rate.RublesPerMeter.Value * request.CutLengthMeters * (double)request.Coefficient,
            MidpointRounding.ToEven);
        if (!double.IsFinite(rounded) || rounded > 922337203685477d)
            return new(PriceCalculationFailure.AmountOutOfRange, "Стоимость превышает диапазон Currency в Access.");
        // Минимум 500 ₽ применяется только при подготовке результата, не здесь.
        return new(new CuttingPriceQuote(table.Name, rate.ColumnName, rate.RublesPerMeter.Value, request.CutLengthMeters,
            request.Coefficient, (decimal)rounded));
    }

    public static decimal CalculatePunchCoefficient(int partCount, int punchCount)
    {
        if (partCount <= 0 || punchCount < 0)
            throw new PriceCalculationException(PriceCalculationFailure.InvalidParameters,
                "Для расчёта коэффициента нужно положительное количество деталей и неотрицательное количество пробивок.");
        var ratio = Math.Round((double)punchCount / partCount, MidpointRounding.ToEven);
        return ratio switch
        {
            <= 1 => 1m, <= 10 => 1m + ((decimal)ratio - 1m) / 10m,
            <= 15 => 2m, <= 20 => 2.1m, <= 30 => 2.2m, <= 40 => 2.3m,
            <= 50 => 2.4m, <= 60 => 2.5m, <= 70 => 2.6m, <= 80 => 2.7m,
            <= 90 => 2.8m, <= 100 => 2.9m, _ => 3m
        };
    }
}

public sealed class CuttingPriceService(IPriceReader reader)
{
    public static async Task<CuttingMachine?> ResolveMachineAsync(IPriceReader reader, string name, CancellationToken cancellationToken = default) =>
        CuttingMachine.LegacyNames.Contains(name) ? null : CuttingMachine.Resolve(name, reader is IMachineCatalogReader catalog
            ? await catalog.ReadMachinesAsync(cancellationToken) : []);

    public async Task<CuttingPriceQuote> CalculateAsync(CuttingPriceRequest request, CancellationToken cancellationToken = default)
    {
        var result = await CalculateResultAsync(request, cancellationToken);
        return result.Quote ?? throw new PriceCalculationException(result.Failure!.Value, result.Error!);
    }

    public async Task<CuttingPriceResult> CalculateResultAsync(CuttingPriceRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var machine = await ResolveMachineAsync(reader, request.CuttingType, cancellationToken);
            var name = CuttingPriceCalculator.SelectTable(request, machine);
            cancellationToken.ThrowIfCancellationRequested();
            var table = await reader.ReadAsync(name, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return CuttingPriceCalculator.CalculateResult(request, table, machine);
        }
        catch (PriceCalculationException exception) { return new(exception.Failure, exception.Message); }
    }
}
