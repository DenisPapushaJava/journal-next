using Jornal.Core.Pricing;
using Jornal.Desktop.Import;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public partial class NewOrderViewModel
{
    private readonly TimeProvider _priceTimeProvider;
    private PricePreviewScheduler _pricePreview = null!;
    internal void StopPricePreview() => _pricePreview.Stop();
    private void InitializePricePreview() => _pricePreview = new(() => RefreshLivePrice(), _priceTimeProvider);
    private void InvalidateLivePrice()
    {
        _quote = null; PriceText = "—"; PriceBreakdown = ""; PriceError = null;
        _pricePreview?.Cancel(); ScheduleLivePrice();
    }
    private void ScheduleLivePrice()
    {
        if (!_thicknessStopped && !IsBatchMode && ThicknessOptions.IsActive && _quote is null) _pricePreview?.Schedule();
    }
    private void FlushLivePrice() { _pricePreview.Cancel(); if (_quote is null) RefreshLivePrice(true); }
    private void RefreshLivePrice(bool saving = false)
    {
        if (_thicknessStopped || !HasPricing || IsBatchMode || IsSaved || RequiresReload || IsImporting || IsEditingPrices ||
            !saving && (SaveCommand.IsRunning || CalculatePriceCommand.IsRunning) || !ThicknessOptions.IsVerified ||
            !PricePreviewInput.TryBuild(CuttingType.Value, Thickness.Value, CutLength.Value, Coefficient.Value,
                MaterialId.Value, RequiresImportedHansFields, out var request)) return;
        if (SpecificationMachineBinding.Validate(ImportPreview, CuttingType.Value, SelectedMachine) is { } mismatch)
        { PriceError = mismatch; return; }
        try
        {
            var name = CuttingPriceCalculator.SelectTable(request, SelectedMachine);
            if (!_thicknessSource.TryGet(name, out var table)) return;
            var result = CuttingPriceCalculator.CalculateResult(request, table!, SelectedMachine);
            if (result.Quote is { } quote) ShowQuote(quote);
            else { _priceFailureKind = result.Failure; HasPriceValidationError = true; PriceError = result.Error; }
        }
        catch (PriceCalculationException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { PriceError = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось рассчитать цену."); }
    }
}
