using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Jornal.Workflow.Desktop;

public sealed partial class WorkflowProfileColorOption(string name, string hex) : ObservableObject
{
    public string Name { get; } = name;
    public string Hex { get; } = hex;
    public uint Argb { get; } = Color.Parse(hex).ToUInt32();
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(hex));
    public IBrush CheckBrush { get; } = Color.Parse(hex) is var c && c.R * .299 + c.G * .587 + c.B * .114 > 155 ? Brushes.Black : Brushes.White;
    public string Description => Name + " · " + Hex + (IsSelected ? " · выбран" : "");
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Description))] private bool _isSelected;
}

public static class WorkflowProfileColors
{
    // Exact presets from the existing Journal palette (alpha.20); separate editor state per Workflow window.
    public static WorkflowProfileColorOption[] Frequent() =>
    [new("Зелёный", "#80FF80"), new("Голубой", "#42C1FF"), new("Сиреневый", "#FF9DFF"), new("Жёлтый", "#FFFF80"), new("Бирюзовый", "#80FFFF")];
    public static WorkflowProfileColorOption[] Other() =>
    [
        new("Мятный", "#A3DEC5"), new("Серебристый", "#C0C0C0"), new("Морской", "#0596BA"), new("Фиолетовый", "#8000FF"),
        new("Оливковый", "#808000"), new("Серо-голубой", "#67A9DA"), new("Оранжевый", "#FF8000"), new("Коралловый", "#FF8080"),
        new("Абрикосовый", "#FF8040"), new("Пыльно-розовый", "#D9A8A6"), new("Персиковый", "#FCE5CD"), new("Кремовый", "#FFF2CC"),
        new("Светло-зелёный", "#D9EAD3"), new("Светло-голубой", "#CFE2F3"), new("Лавандовый", "#D9D2E9"), new("Светло-розовый", "#F4CCCC"),
        new("Песочный", "#D8C6A5"), new("Серо-зелёный", "#ADC9B2"), new("Серо-синий", "#AFC2D7"), new("Серо-сиреневый", "#C3B8D4"),
        new("Графитовый", "#40464F"), new("Тёмно-синий", "#2F5496"), new("Тёмно-зелёный", "#38761D"), new("Тёмно-фиолетовый", "#7030A0")
    ];
}
