using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowSettingsViewModel
{
    private WorkflowResultPricing _resultPricing = new();
    private bool _loadingResultPricing;
    [ObservableProperty] private bool _useResultMinimum;
    [ObservableProperty] private string _resultMinimumText = "500";
    [ObservableProperty] private string? _resultPriceError;

    private void ReloadResultPricing()
    {
        _resultPricing = Journal.ResultPricing; NormalizeResultPriceInput();
    }
    public bool HasResultPriceError => !string.IsNullOrEmpty(ResultPriceError);
    partial void OnResultPriceErrorChanged(string? value) => OnPropertyChanged(nameof(HasResultPriceError));
    public void NormalizeResultPriceInput()
    {
        if (!TryMinimum(out _)) ResultPriceError = null;
        _loadingResultPricing = true;
        try { UseResultMinimum = _resultPricing.UseMinimumPrice; ResultMinimumText = _resultPricing.MinimumPrice.ToString("0", CultureInfo.InvariantCulture); }
        finally { _loadingResultPricing = false; }
    }
    private bool TryMinimum(out decimal value) => decimal.TryParse(ResultMinimumText, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    partial void OnUseResultMinimumChanged(bool value)
    { if (!_loadingResultPricing && CanEdit) SaveResultPricing(_resultPricing with { UseMinimumPrice = value }); }
    partial void OnResultMinimumTextChanged(string value)
    {
        if (_loadingResultPricing || !CanEdit) return;
        if (!TryMinimum(out var minimum)) { ResultPriceError = "Укажите целое неотрицательное число."; return; }
        SaveResultPricing(_resultPricing with { MinimumPrice = minimum });
    }
    private void SaveResultPricing(WorkflowResultPricing value)
    {
        ResultPriceError = Journal.SaveResultPricing(_resultPricing, value);
        if (ResultPriceError is null) _resultPricing = Journal.ResultPricing;
    }
}
