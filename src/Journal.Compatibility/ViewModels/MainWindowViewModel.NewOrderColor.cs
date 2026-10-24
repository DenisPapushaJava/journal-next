using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Desktop.Customization;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private int _technologistColorArgb = NewOrderColorSettings.DefaultColor;
    private HsvColor _technologistSpectrumColor = new(Color.FromUInt32(unchecked((uint)NewOrderColorSettings.DefaultColor)));
    private bool _synchronizingTechnologistSpectrum;
    public int TechnologistColorArgb => _technologistColorArgb;
    public string TechnologistColorHex => "#" + (unchecked((uint)TechnologistColorArgb) & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
    public int EffectiveNewOrderColor => CaptureNewOrderColor().EffectiveColor;
    public HsvColor TechnologistSpectrumColor
    {
        get => _technologistSpectrumColor;
        set
        {
            var opaque = new HsvColor(1, value.H, value.S, value.V);
            if (!SetProperty(ref _technologistSpectrumColor, opaque) || _synchronizingTechnologistSpectrum) return;
            _synchronizingTechnologistSpectrum = true;
            try { TechnologistColorCode = unchecked((int)opaque.ToRgb().ToUInt32()).ToString(CultureInfo.InvariantCulture); }
            finally { _synchronizingTechnologistSpectrum = false; }
        }
    }
    public string NewOrderColorPreviewText => "Пример заявки" + (UseTechnologistColor ? " · " + TechnologistColorHex : "");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(EffectiveNewOrderColor)), NotifyPropertyChangedFor(nameof(NewOrderColorPreviewText)),
        NotifyPropertyChangedFor(nameof(TechnologistColorError)), NotifyPropertyChangedFor(nameof(HasTechnologistColorError))]
    private bool _useTechnologistColor;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TechnologistColorError)), NotifyPropertyChangedFor(nameof(HasTechnologistColorError))]
    private string _technologistColorCode = NewOrderColorSettings.DefaultColor.ToString(CultureInfo.InvariantCulture);
    public string? TechnologistColorError => UseTechnologistColor && TechnologistColorCode is { Length: > 0 } && !TryColor(TechnologistColorCode, out _)
        ? int.TryParse(TechnologistColorCode.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var code) && (unchecked((uint)code) >> 24) == 255
            ? "Этот цвет зарезервирован. Выберите другой." : "Укажите непрозрачный ARGB-цвет."
        : null;
    public bool HasTechnologistColorError => TechnologistColorError is not null;
    public NewOrderColorSettings CaptureNewOrderColor() => new() { Enabled = UseTechnologistColor, ColorArgb = TechnologistColorArgb };
    public void ApplyNewOrderColor(NewOrderColorSettings settings)
    {
        var normalized = settings.Normalize();
        TechnologistColorCode = normalized.ColorArgb.ToString(CultureInfo.InvariantCulture);
        UseTechnologistColor = normalized.Enabled;
    }
    public void CommitTechnologistColorCode() => TechnologistColorCode = TechnologistColorArgb.ToString(CultureInfo.InvariantCulture);
    public void SelectTechnologistColor(int argb)
    { if (NewOrderColorSettings.IsAllowed(argb)) TechnologistColorCode = argb.ToString(CultureInfo.InvariantCulture); }
    partial void OnTechnologistColorCodeChanged(string value)
    {
        if (!TryColor(value, out var argb)) return;
        if (!_synchronizingTechnologistSpectrum)
        {
            var hsv = new HsvColor(Color.FromUInt32(unchecked((uint)argb)));
            if (hsv.S == 0) hsv = new(1, TechnologistSpectrumColor.H, hsv.S, hsv.V);
            _synchronizingTechnologistSpectrum = true;
            try { TechnologistSpectrumColor = hsv; }
            finally { _synchronizingTechnologistSpectrum = false; }
        }
        if (argb == _technologistColorArgb) return;
        _technologistColorArgb = argb;
        OnStatusFilterColorChanged();
        foreach (var option in FrequentTechnologistColors.Concat(OtherTechnologistColors)) option.IsSelected = option.Argb == argb;
        foreach (var name in new[] { nameof(TechnologistColorArgb), nameof(TechnologistColorHex), nameof(EffectiveNewOrderColor), nameof(NewOrderColorPreviewText) }) OnPropertyChanged(name);
    }
    private static bool TryColor(string? text, out int argb) => int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign,
        CultureInfo.InvariantCulture, out argb) && NewOrderColorSettings.IsAllowed(argb);
    public IReadOnlyList<TechnologistColorOption> FrequentTechnologistColors { get; } =
    [new("Зелёный", "#80FF80"), new("Голубой", "#42C1FF"), new("Сиреневый", "#FF9DFF"), new("Жёлтый", "#FFFF80"), new("Бирюзовый", "#80FFFF")];
    public IReadOnlyList<TechnologistColorOption> OtherTechnologistColors { get; } =
    [
        new("Мятный", "#A3DEC5"), new("Серебристый", "#C0C0C0"), new("Морской", "#0596BA"), new("Фиолетовый", "#8000FF"),
        new("Оливковый", "#808000"), new("Серо-голубой", "#67A9DA"), new("Оранжевый", "#FF8000"), new("Коралловый", "#FF8080"),
        new("Абрикосовый", "#FF8040"), new("Пыльно-розовый", "#D9A8A6"), new("Персиковый", "#FCE5CD"), new("Кремовый", "#FFF2CC"),
        new("Светло-зелёный", "#D9EAD3"), new("Светло-голубой", "#CFE2F3"), new("Лавандовый", "#D9D2E9"), new("Светло-розовый", "#F4CCCC"),
        new("Песочный", "#D8C6A5"), new("Серо-зелёный", "#ADC9B2"), new("Серо-синий", "#AFC2D7"), new("Серо-сиреневый", "#C3B8D4"),
        new("Графитовый", "#40464F"), new("Тёмно-синий", "#2F5496"), new("Тёмно-зелёный", "#38761D"), new("Тёмно-фиолетовый", "#7030A0")
    ];
}

public sealed partial class TechnologistColorOption : ObservableObject
{
    public TechnologistColorOption(string name, string hex)
    {
        Name = name; Hex = hex; var color = Color.Parse(hex); Argb = unchecked((int)color.ToUInt32());
        Brush = new ImmutableSolidColorBrush(color);
        CheckBrush = color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 155 ? Brushes.Black : Brushes.White;
        IsSelected = Argb == NewOrderColorSettings.DefaultColor;
    }
    public string Name { get; }
    public string Hex { get; }
    public int Argb { get; }
    public IBrush Brush { get; }
    public IBrush CheckBrush { get; }
    public string Description => $"{Name} · {Hex} · {Argb}" + (IsSelected ? " · выбран" : "");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Description))] private bool _isSelected;
}
