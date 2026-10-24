using Jornal.Core.Pricing;
using Jornal.Infrastructure.Demo;

namespace Jornal.Desktop.ViewModels;

public partial class NewOrderViewModel
{
    private ThicknessPriceSource _thicknessSource = null!;
    private bool _thicknessStopped;
    private bool _applyingThicknessImport;
    public ThicknessOptionsViewModel ThicknessOptions { get; private set; } = null!;
    public Func<PriceEditorViewModel?>? PriceEditorFactory { get; set; }
    public bool IsEditingPrices { get; private set; }
    private PriceCalculationFailure? _priceFailureKind;
    private void InitializeThicknessOptions()
    {
        _thicknessSource = new(() => _creator as IPriceReader ?? (_creator as DemoOrderStore)?.Prices);
        ThicknessOptions = new(Thickness, _thicknessSource,
            () => new(CuttingType.Value, IsHansLaser ? MaterialId.Value.Trim() : "", SelectedMachine, RequiresImportedHansFields), AccessHostHelp.ShowFor);
        ThicknessOptions.ReloadAll = () => CalculatePriceCommand.ExecuteAsync(null);
        ThicknessOptions.Changed += (_, _) =>
        {
            if (ThicknessOptions.IsVerified && ThicknessOptions.CanSave &&
                _priceFailureKind is PriceCalculationFailure.PriceUnavailable or PriceCalculationFailure.InvalidPrice) PriceError = null;
            ScheduleLivePrice();
            OnPropertyChanged(nameof(IsBusy)); SaveCommand.NotifyCanExecuteChanged(); NotifyBatchState();
        };
    }
    public async Task RefreshThicknessOptionsAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (force)
        {
            _thicknessSource.Invalidate(); InvalidateLivePrice();
            foreach (var item in BatchItems.Where(item => item.HasParameters)) item.Form.InvalidateLivePrice();
        }
        await ThicknessOptions.RefreshAsync(cancellationToken);
        await Task.WhenAll(BatchItems.Where(item => item.HasParameters).Select(item => item.Form.ThicknessOptions.RefreshAsync(cancellationToken)));
    }
    public async Task WaitForThicknessOptionsAsync(CancellationToken cancellationToken = default)
    {
        await ThicknessOptions.WaitForLoadAsync(cancellationToken);
        await Task.WhenAll(BatchItems.Where(item => item.IsIncluded).Select(item => item.Form.ThicknessOptions.WaitForLoadAsync(cancellationToken)));
    }
    public PriceEditorViewModel? CreateThicknessPriceEditor(OrderEntryField field)
    {
        if (IsBusy || IsEditingPrices || RequiresReload || field.ThicknessOptions is not { CanOpenPrices: true, TableName: { } table }) return null;
        var reader = _creator as IPriceReader ?? (_creator as DemoOrderStore)?.Prices;
        if (reader is null) return null;
        var form = PriceEditorFactory?.Invoke() ?? new PriceEditorViewModel(reader,
            _creator as IPriceEditor ?? (_creator as DemoOrderStore)?.Prices, StorageDescription, StorageDescription,
            IsLocalCopy ? PriceStorageKind.LocalCopy : IsDatabase ? PriceStorageKind.SharedDatabase : PriceStorageKind.Session);
        form.InitialTableName = table; return form;
    }
    public void SetPriceEditorOpen(bool open)
    {
        IsEditingPrices = open; OnPropertyChanged(nameof(IsBusy)); NotifyBatchState();
        CalculatePriceCommand.NotifyCanExecuteChanged();
    }
    public async Task RefreshAfterPriceEditingAsync(CancellationToken cancellationToken = default)
    {
        InvalidateLivePrice(); await LoadMachinesAsync(cancellationToken);
        await RefreshThicknessOptionsAsync(true, cancellationToken);
    }
    public void StopThicknessOptions()
    {
        if (_thicknessStopped) return;
        _thicknessStopped = true; _pricePreview.Stop();
        ThicknessOptions.Stop();
        foreach (var item in BatchItems) { item.Form._pricePreview.Stop(); item.Form.ThicknessOptions.Stop(); }
        _thicknessSource.Stop();
    }
    public void StartThicknessOptions()
    {
        _pricePreview.Start(); ScheduleLivePrice();
        if (!_thicknessStopped) return;
        _thicknessStopped = false;
        _thicknessSource = new(() => _creator as IPriceReader ?? (_creator as DemoOrderStore)?.Prices);
        InvalidateLivePrice(); ThicknessOptions.Restart(_thicknessSource);
        foreach (var item in BatchItems)
        { item.Form._pricePreview.Start(); item.Form._thicknessSource = _thicknessSource; item.Form.InvalidateLivePrice(); item.Form.ThicknessOptions.Restart(_thicknessSource); }
    }
}
