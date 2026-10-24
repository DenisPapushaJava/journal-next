using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Jornal.Desktop.ViewModels;

[Flags]
public enum OrderStatusFilter
{
    None = 0, Work = 1, Ready = 2, Shipped = 4, Unmarked = 8, Mine = 16,
    All = Work | Ready | Shipped | Unmarked | Mine
}

public sealed partial class StatusFilterOption(OrderStatusFilter value, string label, int? color = null) : ObservableObject
{
    public OrderStatusFilter Value { get; } = value;
    public string Label { get; } = label;
    public int Count { get; internal set; }
    public bool HasColor => color.HasValue;
    public IBrush ColorBrush { get; } = color is { } argb
        ? new ImmutableSolidColorBrush(Color.FromUInt32(unchecked((uint)argb))) : Brushes.Transparent;
    [ObservableProperty] private bool _isChecked;
}

// Черновик меню: изменение чекбоксов не меняет таблицу до явного применения.
public sealed partial class StatusFilterEditorViewModel : ObservableObject
{
    public IReadOnlyList<StatusFilterOption> Options { get; private set; } = [];
    public bool? AllChecked => Options.Count == 0 || Options.All(option => !option.IsChecked) ? false
        : Options.All(option => option.IsChecked) ? true : null;
    public bool CanApply => Options.Any(option => option.IsChecked);
    public OrderStatusFilter Selection => AllChecked == true ? OrderStatusFilter.All
        : Options.Where(option => option.IsChecked).Aggregate(OrderStatusFilter.None, (selection, option) => selection | option.Value);

    public void Begin(OrderStatusFilter selection, bool personalColorEnabled, int personalColor,
        IReadOnlyDictionary<OrderStatusFilter, int>? counts = null)
    {
        foreach (var option in Options) option.PropertyChanged -= OnOptionChanged;
        Options = CreateOptions(personalColorEnabled, personalColor);
        foreach (var option in Options)
        {
            option.Count = counts?.GetValueOrDefault(option.Value) ?? 0;
            option.IsChecked = (selection & option.Value) != 0;
            option.PropertyChanged += OnOptionChanged;
        }
        OnPropertyChanged(nameof(Options)); NotifySelection();
    }

    public static IReadOnlyList<StatusFilterOption> CreateOptions(bool personalColorEnabled, int personalColor) =>
        personalColorEnabled
            ? [new(OrderStatusFilter.Mine, "Мои заявки", personalColor), new(OrderStatusFilter.Unmarked, "Другие без статуса"),
                new(OrderStatusFilter.Work, "В работе"), new(OrderStatusFilter.Ready, "Готово"), new(OrderStatusFilter.Shipped, "Отгружено")]
            : [new(OrderStatusFilter.Unmarked, "Без статуса"), new(OrderStatusFilter.Work, "В работе"),
                new(OrderStatusFilter.Ready, "Готово"), new(OrderStatusFilter.Shipped, "Отгружено")];

    [RelayCommand]
    private void ToggleAll()
    {
        var select = AllChecked != true;
        foreach (var option in Options) option.IsChecked = select;
        NotifySelection();
    }
    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(StatusFilterOption.IsChecked)) NotifySelection(); }
    private void NotifySelection()
    { OnPropertyChanged(nameof(AllChecked)); OnPropertyChanged(nameof(CanApply)); }
}
