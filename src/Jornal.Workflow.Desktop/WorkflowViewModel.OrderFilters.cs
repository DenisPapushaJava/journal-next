using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    [ObservableProperty] private bool _onlyMyOrders;
    [ObservableProperty] private bool _ordersWithBending;
    [ObservableProperty] private bool _ordersWithChamfer;
    private int _workFilterIndex;
    public static string[] WorkFilterOptions { get; } = ["Все заявки", "Ещё не переданы", "В работе"];
    public int WorkFilterIndex
    {
        get => _workFilterIndex;
        set { if (value is >= 0 and <= 2 && SetProperty(ref _workFilterIndex, value)) OrderFiltersChanged(); }
    }
    public bool CanFilterMyOrders => CurrentEmployee is { CanWork: true };
    public int ActiveOrderFilterCount => (OnlyMyOrders ? 1 : 0) + (WorkFilterIndex != 0 ? 1 : 0) +
        (OrdersWithBending ? 1 : 0) + (OrdersWithChamfer ? 1 : 0);
    public bool HasOrderFilters => ActiveOrderFilterCount > 0;
    public string OrderFilterCaption => HasOrderFilters ? "Фильтры · " + ActiveOrderFilterCount : "Фильтры";
    public string OrderFilterHint => (HasOrderFilters ? string.Join(" · ", ActiveOrderFilterNames()) : "Все заявки выбранного периода") +
        "\nУсловия применяются вместе с датой и поиском.";
    private WorkflowOrderFilter CurrentOrderFilter => new(OnlyMyOrders ? CurrentEmployee?.Id ?? Guid.Empty : null,
        (WorkflowWorkFilter)WorkFilterIndex, OrdersWithBending, OrdersWithChamfer);

    private IEnumerable<string> ActiveOrderFilterNames()
    {
        if (OnlyMyOrders) yield return CanFilterMyOrders ? "Мои заявки" : "Мои заявки · выберите технолога";
        if (WorkFilterIndex != 0) yield return WorkFilterOptions[WorkFilterIndex];
        if (OrdersWithBending) yield return "С гибкой";
        if (OrdersWithChamfer) yield return "С фаской";
    }
    partial void OnOnlyMyOrdersChanged(bool value) => OrderFiltersChanged();
    partial void OnOrdersWithBendingChanged(bool value) => OrderFiltersChanged();
    partial void OnOrdersWithChamferChanged(bool value) => OrderFiltersChanged();

    private void NotifyOrderFilters()
    {
        foreach (var property in new[] { nameof(CanFilterMyOrders), nameof(ActiveOrderFilterCount), nameof(HasOrderFilters),
            nameof(OrderFilterCaption), nameof(OrderFilterHint) }) OnPropertyChanged(property);
    }
    private void OrderFiltersChanged()
    {
        NotifyOrderFilters();
        if (_refreshing) return;
        if (_store is IWorkflowQueryStore) ScheduleDatabaseSearch(); else RefreshOrders(preserveRows: true);
    }
    private void ResetQuickOrderFilterValues()
    { OnlyMyOrders = false; WorkFilterIndex = 0; OrdersWithBending = false; OrdersWithChamfer = false; }

    [RelayCommand]
    private void ClearQuickOrderFilters()
    {
        if (!HasOrderFilters) return;
        _refreshing = true;
        try { ResetQuickOrderFilterValues(); } finally { _refreshing = false; }
        OrderFiltersChanged();
    }
}
