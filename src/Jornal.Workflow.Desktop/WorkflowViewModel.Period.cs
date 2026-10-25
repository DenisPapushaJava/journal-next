using CommunityToolkit.Mvvm.Input;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private DateTime? _filterDate = DateTime.Today;
    private bool _hasLoadedOrders;

    public DateTime? FilterDate
    {
        get => _filterDate;
        set
        {
            if (!SetProperty(ref _filterDate, value?.Date)) return;
            OnPropertyChanged(nameof(IsTodaySelected));
            OnPropertyChanged(nameof(IsAllDatesSelected));
            if (!_refreshing)
            {
                if (_store is Infrastructure.IWorkflowQueryStore) ScheduleDatabaseSearch(); else RefreshOrders();
                // No card was open: leave rows unselected so the first click opens one.
                if (!IsOrderOpen) SelectedOrder = null;
            }
        }
    }
    public bool IsTodaySelected
    {
        get => FilterDate == DateTime.Today;
        set { if (value) FilterDate = DateTime.Today; }
    }
    public bool IsAllDatesSelected
    {
        get => FilterDate is null;
        set { if (value) FilterDate = null; }
    }
    public bool IsOrderListEmpty => _hasLoadedOrders && !_readingDatabase && (!IsBusy || _savingProjectInclusion) && Orders.Count == 0;
    public string EmptyOrderListText => Search.Trim().Length > 0 ? "Заявки не найдены" :
        FilterDate is { } date ? "Нет заявок за " + date.ToString("dd.MM.yyyy") : "Заявок пока нет";

    [RelayCommand] private void Today() => FilterDate = DateTime.Today;
    [RelayCommand] private void AllDates() => FilterDate = null;

    private bool IsInPeriod(Core.WorkflowOrder order) => FilterDate is not { } date || order.CreatedAt.ToLocalTime().Date == date;
    private void NotifyOrderList()
    {
        OnPropertyChanged(nameof(IsOrderListEmpty));
        OnPropertyChanged(nameof(EmptyOrderListText));
        OnPropertyChanged(nameof(SearchSummary));
    }
}
