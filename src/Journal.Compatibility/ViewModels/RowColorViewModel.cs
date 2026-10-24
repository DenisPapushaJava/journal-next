using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public partial class RowColorViewModel : ObservableObject
{
    private readonly Func<int, Task<bool>> _confirm;
    private readonly Func<string?> _failure;
    private readonly Func<bool> _canConfirm;
    private HsvColor _spectrumColor = new(1, 0, 0, 1);
    private bool _synchronizingSpectrum;
    public RowColorViewModel(int initial, int count, IEnumerable<TechnologistColorOption> colors,
        Func<int, Task<bool>> confirm, Func<string?> failure, Func<bool> canConfirm)
    {
        _confirm = confirm; _failure = failure; _canConfirm = canConfirm;
        Count = count;
        Colors = colors.Select(option => new TechnologistColorOption(option.Name, option.Hex)).ToArray();
        Code = "#" + (unchecked((uint)initial) & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
    }
    public int Count { get; }
    public string Title => "Цвет выбранных заявок";
    public string ConfirmLabel => "Применить";
    public string Summary => $"Выбрано заявок: {Count}";
    public IReadOnlyList<TechnologistColorOption> Colors { get; }
    [ObservableProperty] private string _code = "";
    [ObservableProperty] private int _argb;
    public HsvColor SpectrumColor
    {
        get => _spectrumColor;
        set
        {
            var opaque = new HsvColor(1, value.H, value.S, value.V);
            if (!SetProperty(ref _spectrumColor, opaque) || _synchronizingSpectrum) return;
            _synchronizingSpectrum = true;
            try { Select(unchecked((int)opaque.ToRgb().ToUInt32())); }
            finally { _synchronizingSpectrum = false; }
        }
    }
    public string Hex => "#" + (unchecked((uint)Argb) & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
    public IBrush PreviewBrush => new SolidColorBrush(Color.FromUInt32(unchecked((uint)Argb)));
    public bool IsValid => TryParse(Code, out _);
    public string? ValidationError => IsValid ? null : "Укажите цвет #RRGGBB или непрозрачный ARGB.";
    public bool HasValidationError => ValidationError is not null;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    public bool HasError => Error is not null;
    public bool CanConfirm => !IsBusy && IsValid && _canConfirm();
    public event EventHandler? Confirmed;
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanConfirm)); ConfirmCommand.NotifyCanExecuteChanged(); }
    partial void OnArgbChanged(int value) { OnPropertyChanged(nameof(Hex)); OnPropertyChanged(nameof(PreviewBrush)); }
    partial void OnCodeChanged(string value)
    {
        if (TryParse(value, out var argb))
        {
            Argb = argb; foreach (var option in Colors) option.IsSelected = option.Argb == argb;
            if (!_synchronizingSpectrum)
            {
                var hsv = new HsvColor(Color.FromUInt32(unchecked((uint)argb)));
                // Серый/белый/чёрный не должны сбрасывать выбранный оттенок радужной полосы.
                if (hsv.S == 0) hsv = new(1, SpectrumColor.H, hsv.S, hsv.V);
                _synchronizingSpectrum = true;
                try { SpectrumColor = hsv; }
                finally { _synchronizingSpectrum = false; }
            }
        }
        foreach (var name in new[] { nameof(IsValid), nameof(ValidationError), nameof(HasValidationError), nameof(CanConfirm) }) OnPropertyChanged(name);
        ConfirmCommand.NotifyCanExecuteChanged();
    }
    public void Select(int color) => Code = "#" + (unchecked((uint)color) & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (!CanConfirm) return;
        Error = null; IsBusy = true; var success = false;
        try { success = await _confirm(Argb); if (!success) Error = _failure() ?? "Не удалось применить цвет."; }
        catch (Exception exception) { Error = AppDiagnostics.Describe(DiagnosticOperation.OrderBatch, exception, "Не удалось применить цвет."); }
        finally { IsBusy = false; }
        if (success) Confirmed?.Invoke(this, EventArgs.Empty);
    }
    public static bool TryParse(string? text, out int argb)
    {
        text = text?.Trim();
        if (text is { Length: 7 } && text[0] == '#' && uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
        { argb = unchecked((int)(rgb | 0xFF000000)); return true; }
        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out argb) && (unchecked((uint)argb) >> 24) == 255;
    }
}
