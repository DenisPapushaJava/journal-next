using System.Globalization;
using Jornal.Core.Pricing;
using Jornal.Desktop.Import;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public partial class EditOrderViewModel
{
    private PricePreviewScheduler _pricePreview = null!;
    internal void StopPricePreview() => _pricePreview.Stop();
    private bool _refreshPriceRequested;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _priceBreakdown = "";
    public bool ShowStoredPrice => PriceText != StoredPriceText;
    partial void OnPriceTextChanged(string value) => OnPropertyChanged(nameof(ShowStoredPrice));
    private bool TryPreviewRequest(out CuttingPriceRequest request) => PricePreviewInput.TryBuild(CuttingType.Value,
        Thickness.Value, CutLength.Value, Coefficient.Value, MaterialId.Value,
        IsHansLaser && ImportPreview?.Report.Profile == CuttingMachine.HansProfile, out request);
    private bool PriceInputsChanged
    {
        get
        {
            var original = _expected.ToSummary();
            return !TryPreviewRequest(out var request) || request != new CuttingPriceRequest(original.CuttingType,
                original.ThicknessMillimeters ?? 0, original.CutLengthMeters ?? 0, original.Coefficient ?? 0,
                original.CuttingType == "HansLaser" ? original.MaterialId : "");
        }
    }
    private bool NeedsPricePreview => _refreshPriceRequested || ImportPreview is not null || PriceInputsChanged;
    private void InitializePricePreview(TimeProvider? timeProvider) => _pricePreview = new(() => RefreshLivePrice(), timeProvider ?? TimeProvider.System);
    private void InvalidateLivePrice()
    {
        _quote = null; PricePreviewError = null; PriceText = NeedsPricePreview ? "—" : StoredPriceText; PriceBreakdown = "";
        _pricePreview?.Cancel(); ScheduleLivePrice();
    }
    private void ScheduleLivePrice()
    { if (!_thicknessStopped && NeedsPricePreview && ThicknessOptions.IsActive && _quote is null) _pricePreview?.Schedule(); }
    private void FlushLivePrice() { _pricePreview.Cancel(); if (_quote is null) RefreshLivePrice(true); }
    private void RefreshLivePrice(bool saving = false)
    {
        if (_thicknessStopped || !NeedsPricePreview || RequiresReload || SavedRecord is not null || IsImporting || IsEditingPrices ||
            !saving && (SaveCommand.IsRunning || CalculateCommand.IsRunning || RefreshCommand.IsRunning) ||
            !ThicknessOptions.IsVerified || !TryPreviewRequest(out var request)) return;
        if (SpecificationMachineBinding.Validate(ImportPreview, CuttingType.Value, SelectedMachine) is { } mismatch)
        { PricePreviewError = mismatch; return; }
        try
        {
            var name = CuttingPriceCalculator.SelectTable(request, SelectedMachine);
            if (!_thicknessSource.TryGet(name, out var table)) return;
            var result = CuttingPriceCalculator.CalculateResult(request, table!, SelectedMachine);
            if (result.Quote is { } quote) ShowQuote(quote);
            else PricePreviewError = result.Error;
        }
        catch (PriceCalculationException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { PricePreviewError = AppDiagnostics.Describe(DiagnosticOperation.PriceRead, exception, "Не удалось рассчитать цену."); }
    }
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty, CommunityToolkit.Mvvm.ComponentModel.NotifyPropertyChangedFor(nameof(HasPricePreviewError))]
    private string? _pricePreviewError;
    public bool HasPricePreviewError => PricePreviewError is not null;
    private static string FormatPriceBreakdown(CuttingPriceQuote quote)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        return $"{quote.RublesPerMeter.ToString("N0", culture)} ₽/м × {quote.CutLengthMeters} м × {quote.Coefficient.ToString(culture)}";
    }
}
