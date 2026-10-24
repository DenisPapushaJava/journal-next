using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Core.Results;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private ResultRowOrder _resultRowOrder;
    public ResultRowOrder ResultRowOrder
    {
        get => _resultRowOrder;
        set { if (SetProperty(ref _resultRowOrder, Enum.IsDefined(value) ? value : default)) OnPropertyChanged(nameof(ResultRowOrderIndex)); }
    }
    public IReadOnlyList<string> ResultRowOrders { get; } = ["По толщине и материалу", "По порядку выбора"];
    public int ResultRowOrderIndex { get => (int)ResultRowOrder; set => ResultRowOrder = value == 1 ? ResultRowOrder.SelectionOrder : ResultRowOrder.ThicknessAndMaterial; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ResultFontSizePixels))]
    private decimal _resultFontSizePoints = ResultTextStyle.DefaultSize;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ResultTextBrush))]
    private string _resultTextColor = ResultTextStyle.DefaultColor;

    public double ResultFontSizePixels => (double)ResultFontSizePoints * 96 / 72;
    public IReadOnlyList<decimal> ResultFontSizes => ResultTextStyle.FontSizes;
    public IBrush ResultTextBrush => new ImmutableSolidColorBrush(Color.Parse(ResultTextColor));
    public IBrush ResultFeedbackBrush => new ImmutableSolidColorBrush(Color.Parse(HasResultError ? "#A83632" : "#5D6777"));
    public string ResultColorDescription => $"Цвет текста: {ResultTextColor}";
    public ResultTextStyle CaptureResultStyle() => new ResultTextStyle
        { FontSizePoints = ResultFontSizePoints, ColorHex = ResultTextColor }.Normalize();
    public void ApplyResultStyle(ResultTextStyle style)
    {
        var normalized = style.Normalize();
        ResultFontSizePoints = normalized.FontSizePoints;
        ResultTextColor = normalized.ColorHex;
    }

    partial void OnResultFontSizePointsChanged(decimal value)
    {
        var normalized = CaptureResultStyle().FontSizePoints;
        if (value != normalized) ResultFontSizePoints = normalized;
    }

    partial void OnResultTextColorChanged(string value)
    {
        var normalized = CaptureResultStyle().ColorHex;
        if (value != normalized) { ResultTextColor = normalized; return; }
        foreach (var option in ResultColors) option.IsSelected = option.Hex == normalized;
        OnPropertyChanged(nameof(ResultColorDescription));
    }

    public IReadOnlyList<ResultColorOption> ResultColors { get; } =
    [
        new("Тёмный", "#202124"), new("Серый", "#616161"), new("Красный", "#A61C00"), new("Оранжевый", "#C65911"),
        new("Золотистый", "#997300"), new("Зелёный", "#38761D"), new("Синий", "#2F5496"), new("Фиолетовый", "#7030A0"),
        new("Светло-серый", "#9E9E9E"), new("Белый", "#FFFFFF"), new("Светло-красный", ResultTextStyle.DefaultColor, true), new("Персиковый", "#E69138"),
        new("Жёлтый", "#F1C232"), new("Светло-зелёный", "#70AD47"), new("Голубой", "#5B9BD5"), new("Сиреневый", "#8E7CC3"),
        new("Серебристый", "#D9D9D9"), new("Розовый", "#EA9999"), new("Бледно-красный", "#F4CCCC"), new("Светло-персиковый", "#FCE5CD"),
        new("Светло-жёлтый", "#FFF2CC"), new("Бледно-зелёный", "#D9EAD3"), new("Светло-голубой", "#CFE2F3"), new("Бледно-сиреневый", "#D9D2E9")
    ];
}

public partial class ResultColorOption(string name, string hex, bool selected = false) : ObservableObject
{
    public string Name { get; } = name;
    public string Hex { get; } = hex;
    public IBrush Brush { get; } = new ImmutableSolidColorBrush(Color.Parse(hex));
    public IBrush CheckBrush { get; } = Color.Parse(hex) is var color && color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 155
        ? Brushes.Black : Brushes.White;
    public string Description => $"{Name} · {Hex}" + (IsSelected ? " · выбран" : "");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Description))] private bool _isSelected = selected;
}
