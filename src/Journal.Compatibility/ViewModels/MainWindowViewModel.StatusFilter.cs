using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatusFilter)),
        NotifyPropertyChangedFor(nameof(StatusFilterSummary)), NotifyPropertyChangedFor(nameof(StatusFilterDescription))]
    private OrderStatusFilter _statusFilter = OrderStatusFilter.All;

    public StatusFilterEditorViewModel StatusFilterEditor { get; } = new();
    private OrderStatusFilter AvailableStatuses => UseTechnologistColor ? OrderStatusFilter.All : OrderStatusFilter.All & ~OrderStatusFilter.Mine;
    public bool HasStatusFilter => (StatusFilter & AvailableStatuses) != AvailableStatuses;
    public string StatusFilterSummary
    {
        get
        {
            var options = StatusFilterEditorViewModel.CreateOptions(UseTechnologistColor, TechnologistColorArgb);
            return $"Статус: {options.Count(option => (StatusFilter & option.Value) != 0)} из {options.Count}";
        }
    }
    public string StatusFilterDescription => HasStatusFilter ? "Статус: " + string.Join(", ",
        StatusFilterEditorViewModel.CreateOptions(UseTechnologistColor, TechnologistColorArgb)
            .Where(option => (StatusFilter & option.Value) != 0).Select(option => option.Label)) : "Фильтр статуса";

    public void BeginStatusFilterEdit()
    {
        CancelRefreshForFilter();
        var counts = _allOrders.Where(order => MatchesSearchAndDate(order) && MatchesColumnFilters(order))
            .GroupBy(StatusCategory).ToDictionary(group => group.Key, group => group.Count());
        StatusFilterEditor.Begin(StatusFilter, UseTechnologistColor, TechnologistColorArgb, counts);
    }
    public bool ApplyStatusFilterEdit()
    {
        if (!CanBrowseJournal || !StatusFilterEditor.CanApply) return false;
        StatusFilter = StatusFilterEditor.Selection;
        return true;
    }
    [RelayCommand] private void ClearStatusFilter() => StatusFilter = OrderStatusFilter.All;
    partial void OnStatusFilterChanged(OrderStatusFilter value) => ApplySearch();

    private OrderStatusFilter StatusCategory(OrderSummary order) => order.Color switch
        {
            OrderColors.Work => OrderStatusFilter.Work, OrderColors.Ready => OrderStatusFilter.Ready,
            OrderColors.Shipped => OrderStatusFilter.Shipped,
            _ => UseTechnologistColor && order.Color == TechnologistColorArgb ? OrderStatusFilter.Mine : OrderStatusFilter.Unmarked
        };
    private bool MatchesStatusFilter(OrderSummary order) => (StatusFilter & StatusCategory(order)) != 0;

    partial void OnUseTechnologistColorChanged(bool value)
    {
        var selection = StatusFilter;
        if (selection != OrderStatusFilter.All)
        {
            // При разделении «Без статуса» сохраняем обе части прежнего набора.
            if (value && selection.HasFlag(OrderStatusFilter.Unmarked)) selection |= OrderStatusFilter.Mine;
            if (!value) selection &= ~OrderStatusFilter.Mine;
            if (selection == OrderStatusFilter.None || (selection & AvailableStatuses) == AvailableStatuses) selection = OrderStatusFilter.All;
        }
        if (selection != StatusFilter) StatusFilter = selection;
        else if (HasStatusFilter && selection.HasFlag(OrderStatusFilter.Unmarked)) ApplySearch();
        NotifyStatusFilterContext();
    }
    private void OnStatusFilterColorChanged()
    {
        if (HasStatusFilter && (StatusFilter & (OrderStatusFilter.Mine | OrderStatusFilter.Unmarked)) != 0) ApplySearch();
        NotifyStatusFilterContext();
    }
    private void NotifyStatusFilterContext()
    {
        OnPropertyChanged(nameof(HasStatusFilter)); OnPropertyChanged(nameof(StatusFilterSummary)); OnPropertyChanged(nameof(StatusFilterDescription));
        NotifyFilterContextChanged();
    }
}
