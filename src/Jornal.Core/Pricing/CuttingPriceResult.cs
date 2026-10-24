namespace Jornal.Core.Pricing;

// Ожидаемый отказ расчёта можно показать в форме без выбрасывания исключения.
public sealed record CuttingPriceResult
{
    internal CuttingPriceResult(CuttingPriceQuote quote) => Quote = quote;
    internal CuttingPriceResult(PriceCalculationFailure failure, string error) { Failure = failure; Error = error; }
    public CuttingPriceQuote? Quote { get; }
    public PriceCalculationFailure? Failure { get; }
    public string? Error { get; }
}
