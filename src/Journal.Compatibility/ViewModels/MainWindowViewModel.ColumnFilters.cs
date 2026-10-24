using System.Globalization;
using Jornal.Core.Orders;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private IReadOnlyList<OrderColumnFilterViewModel>? _columnFilters;
    public IReadOnlyList<OrderColumnFilterViewModel> ColumnFilters => _columnFilters ??=
        Enum.GetValues<OrderFilterColumn>().Select(column => new OrderColumnFilterViewModel(column,
            BuildColumnFilterOptions, () => CanBrowseJournal, ApplySearch, CancelRefreshForFilter)).ToArray();
    private void CancelRefreshForFilter() { if (IsRefreshingOrders) CancelOrderLoading(); }
    public OrderColumnFilterViewModel ManagerFilter => ColumnFilters.Single(filter => filter.Column == OrderFilterColumn.Manager);
    public OrderColumnFilterViewModel CuttingTypeFilter => ColumnFilters.Single(filter => filter.Column == OrderFilterColumn.CuttingType);
    public OrderColumnFilterViewModel MaterialFilter => ColumnFilters.Single(filter => filter.Column == OrderFilterColumn.Material);
    public OrderColumnFilterViewModel ThicknessFilter => ColumnFilters.Single(filter => filter.Column == OrderFilterColumn.Thickness);
    public OrderColumnFilterViewModel BendingFilter => ColumnFilters.Single(filter => filter.Column == OrderFilterColumn.Bending);
    public bool HasAnyColumnFilter => HasStatusFilter || ColumnFilters.Any(filter => filter.HasFilter);
    public IReadOnlyList<AppliedTableFilter> ActiveTableFilters =>
        (HasStatusFilter ? new[] { new AppliedTableFilter(this, StatusFilterSummary, StatusFilterDescription,
            "Снять фильтр статуса", ClearStatusFilterCommand) } : [])
        .Concat(ColumnFilters.Where(filter => filter.HasFilter).Select(filter => new AppliedTableFilter(filter,
            filter.Summary, filter.Description, filter.ClearLabel, filter.ClearCommand))).ToArray();
    public int FilterContextVersion { get; private set; }

    private bool MatchesColumnFilters(OrderSummary order, OrderFilterColumn? except = null) =>
        ColumnFilters.All(filter => filter.Column == except || filter.Matches(order));
    private bool MatchesSearchAndDate(OrderSummary order) =>
        (!FilterDate.HasValue || order.OrderDate == DateOnly.FromDateTime(FilterDate.Value)) && _searchQuery.Matches(order);

    private ColumnFilterChoices BuildColumnFilterOptions(OrderFilterColumn column)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var order in _allOrders)
        {
            var key = OrderColumnFilterViewModel.Key(column, order);
            allKeys.Add(key);
            if (!MatchesSearchAndDate(order) || !MatchesStatusFilter(order) || !MatchesColumnFilters(order, column)) continue;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        // Даже исчезнувшее/несовместимое выбранное значение можно снять из меню.
        foreach (var key in ColumnFilters.Single(filter => filter.Column == column).SelectedKeys) counts.TryAdd(key, 0);
        var options = counts.Select(pair => new ColumnFilterOption(pair.Key,
            OrderColumnFilterViewModel.Label(column, pair.Key), pair.Value));
        var sorted = column == OrderFilterColumn.Thickness
            ? options.OrderBy(option => option.Key.Length == 0 ? decimal.MinValue : decimal.Parse(option.Key, CultureInfo.InvariantCulture)).ToArray()
            : column == OrderFilterColumn.Bending
                ? options.OrderBy(option => option.Key switch { "yes" => 0, "no" => 1, _ => 2 }).ToArray()
                : options.OrderBy(option => option.Key, StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), true)).ToArray();
        return new(sorted, allKeys.Count);
    }
    private void NotifyFilterContextChanged()
    {
        FilterContextVersion++;
        OnPropertyChanged(nameof(FilterContextVersion));
        OnPropertyChanged(nameof(HasAnyColumnFilter));
        OnPropertyChanged(nameof(ActiveTableFilters));
        foreach (var filter in ColumnFilters) filter.NotifyContextChanged();
    }
}
