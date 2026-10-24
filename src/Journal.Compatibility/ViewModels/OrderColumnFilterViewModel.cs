using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;

namespace Jornal.Desktop.ViewModels;

public enum OrderFilterColumn { Manager, CuttingType, Material, Thickness, Bending }
public sealed record ColumnFilterChoices(IReadOnlyList<ColumnFilterOption> Options, int TotalValueCount);

public sealed partial class ColumnFilterOption(string key, string label, int count) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    public int Count { get; } = count;
    [ObservableProperty] private bool _isChecked;
}

public sealed partial class ColumnFilterEditorViewModel : ObservableObject
{
    private HashSet<string>? _initialSelection;
    private bool _dirty;
    private bool _allRequested;
    private int _totalValueCount;
    public IReadOnlyList<ColumnFilterOption> Options { get; private set; } = [];
    [ObservableProperty] private string _searchText = string.Empty;
    public IEnumerable<ColumnFilterOption> VisibleOptions => Options.Where(option =>
        option.Label.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase));
    public bool HasNoMatches => !VisibleOptions.Any();
    public bool? AllChecked => Options.Count == 0 || Options.All(option => !option.IsChecked)
        ? false : Options.All(option => option.IsChecked) ? true : null;
    public bool CanApply => Options.Any(option => option.IsChecked);
    // Поиск скрывает только пункты меню. Выбранные вне поиска не теряются.
    public HashSet<string>? Selection
    {
        get
        {
            if (!_dirty) return _initialSelection;
            if (_allRequested || AllChecked == true && Options.Count == _totalValueCount) return null;
            return Options.Where(option => option.IsChecked).Select(option => option.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Begin(ColumnFilterChoices choices, HashSet<string>? selection)
    {
        foreach (var option in Options) option.PropertyChanged -= OnOptionChanged;
        _initialSelection = selection?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Options = choices.Options;
        _totalValueCount = choices.TotalValueCount;
        foreach (var option in Options)
        {
            option.IsChecked = selection is null || selection.Contains(option.Key);
            option.PropertyChanged += OnOptionChanged;
        }
        _dirty = false;
        _allRequested = false;
        SearchText = string.Empty;
        OnPropertyChanged(nameof(Options));
        RefreshVisible();
        NotifySelection();
    }
    partial void OnSearchTextChanged(string value) => RefreshVisible();
    private void RefreshVisible()
    { OnPropertyChanged(nameof(VisibleOptions)); OnPropertyChanged(nameof(HasNoMatches)); }
    [RelayCommand]
    private void ToggleAll()
    {
        var select = AllChecked != true;
        foreach (var option in Options) option.IsChecked = select;
        _allRequested = select;
        _dirty = true;
        NotifySelection();
    }
    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ColumnFilterOption.IsChecked)) return;
        _dirty = true;
        _allRequested = false;
        NotifySelection();
    }
    private void NotifySelection()
    { OnPropertyChanged(nameof(AllChecked)); OnPropertyChanged(nameof(CanApply)); }
}

public sealed partial class OrderColumnFilterViewModel(OrderFilterColumn column,
    Func<OrderFilterColumn, ColumnFilterChoices> optionsFactory,
    Func<bool> canUse, Action changed, Action? beginEdit = null) : ObservableObject
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private const string DecimalFormat = "0.############################";
    private HashSet<string>? _selection;
    public OrderFilterColumn Column { get; } = column;
    public string Title => Column switch
    {
        OrderFilterColumn.Manager => "Менеджер",
        OrderFilterColumn.CuttingType => "Вид резки", OrderFilterColumn.Material => "Материал",
        OrderFilterColumn.Thickness => "Толщина", _ => "Гибка"
    };
    public string Header => Column == OrderFilterColumn.Thickness ? "Толщ., мм" : Title;
    public string FilterLabel => "Фильтр: " + Title;
    public string ClearLabel => "Снять фильтр: " + Title;
    public bool CanUseJournal => canUse();
    public bool HasFilter => _selection is not null;
    public string Summary => Title + ": " + (_selection is { Count: 1 }
        ? Label(Column, _selection.Single()) : _selection?.Count.ToString(Russian));
    public string Description => _selection is null ? FilterLabel : Title + ": " +
        string.Join(", ", _selection.Select(key => Label(Column, key)));
    public ColumnFilterEditorViewModel Editor { get; } = new();
    public void BeginEdit() { beginEdit?.Invoke(); Editor.Begin(optionsFactory(Column), _selection); }
    public bool ApplyEdit()
    {
        if (!CanUseJournal || !Editor.CanApply) return false;
        var selection = Editor.Selection;
        if (_selection is null && selection is null || _selection is not null && selection is not null && _selection.SetEquals(selection))
            return true;
        _selection = selection?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        NotifySelection();
        changed();
        return true;
    }
    [RelayCommand]
    private void Clear()
    {
        if (!CanUseJournal || !HasFilter) return;
        Reset();
        changed();
    }
    internal void Reset() { _selection = null; NotifySelection(); }
    internal void NotifyAvailability() => OnPropertyChanged(nameof(CanUseJournal));
    public int ContextVersion { get; private set; }
    internal void NotifyContextChanged() { ContextVersion++; OnPropertyChanged(nameof(ContextVersion)); }
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(HasFilter)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(Description));
    }
    internal bool Matches(OrderSummary order) => _selection is null || _selection.Contains(Key(Column, order));
    internal IEnumerable<string> SelectedKeys => _selection ?? Enumerable.Empty<string>();
    internal static string Key(OrderFilterColumn column, OrderSummary order) => column switch
    {
        OrderFilterColumn.Manager => order.ManagerName?.Trim() ?? "",
        OrderFilterColumn.CuttingType => order.CuttingType?.Trim() ?? "",
        OrderFilterColumn.Material => order.MaterialName?.Trim() ?? "",
        OrderFilterColumn.Thickness => order.ThicknessMillimeters?.ToString(DecimalFormat, CultureInfo.InvariantCulture) ?? "",
        _ => order.AnnotationsUnavailable || order.AnnotationsNeedReview ? "unknown" : order.HasBending ? "yes" : "no"
    };
    internal static string Label(OrderFilterColumn column, string key) => column switch
    {
        OrderFilterColumn.Bending => key switch { "yes" => "Есть", "no" => "Нет", _ => "Неизвестно" },
        OrderFilterColumn.Thickness when key.Length > 0 => decimal.Parse(key, CultureInfo.InvariantCulture).ToString(DecimalFormat, Russian),
        _ => key.Length == 0 ? "(Пусто)" : key
    };
}
