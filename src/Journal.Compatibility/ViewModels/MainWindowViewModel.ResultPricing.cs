using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Core.Results;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private decimal _resultMinimumPrice = ResultPriceSettings.DefaultMinimum;
    public decimal ResultMinimumPrice => _resultMinimumPrice;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ResultMinimumPriceError)), NotifyPropertyChangedFor(nameof(HasResultMinimumPriceError))]
    private bool _useResultMinimumPrice = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ResultMinimumPriceError)), NotifyPropertyChangedFor(nameof(HasResultMinimumPriceError))]
    private string _resultMinimumPriceText = ResultPriceSettings.DefaultMinimum.ToString(CultureInfo.InvariantCulture);
    public string? ResultMinimumPriceError => UseResultMinimumPrice && ResultMinimumPriceText is { Length: > 0 } &&
        !TryReadResultMinimum(ResultMinimumPriceText, out _) ? "Укажите сумму целыми рублями от 0." : null;
    public bool HasResultMinimumPriceError => ResultMinimumPriceError is not null;
    public ResultPriceSettings CaptureResultPricing() => new() { UseMinimumPrice = UseResultMinimumPrice, MinimumPrice = ResultMinimumPrice };
    public void ApplyResultPricing(ResultPriceSettings settings)
    {
        var normalized = settings.Normalize();
        UseResultMinimumPrice = normalized.UseMinimumPrice;
        ResultMinimumPriceText = normalized.MinimumPrice.ToString("0", CultureInfo.InvariantCulture);
    }
    partial void OnResultMinimumPriceTextChanged(string value)
    {
        if (!TryReadResultMinimum(value, out var amount) || amount == _resultMinimumPrice) return;
        _resultMinimumPrice = amount;
        OnPropertyChanged(nameof(ResultMinimumPrice));
    }
    // Незаконченный/ошибочный ввод не заменяет последнее допустимое сохранённое значение.
    public void CommitResultMinimumPrice() => ResultMinimumPriceText = ResultMinimumPrice.ToString("0", CultureInfo.InvariantCulture);
    private static bool TryReadResultMinimum(string? text, out decimal amount) =>
        decimal.TryParse(text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out amount) && amount >= 0;
}
