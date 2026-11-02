using CommunityToolkit.Mvvm.Input;
using System.Globalization;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private DateTime? _filterDate = DateTime.Today;
    private bool _isMonthPeriod;
    private bool _hasLoadedOrders;

    public DateTime? FilterDate
    {
        get => _filterDate;
        set => SetPeriod(value?.Date, false);
    }
    public bool IsMonthPeriod => _isMonthPeriod;
    public string PeriodCaption => FilterDate is { } date ? IsMonthPeriod
        ? CultureInfo.GetCultureInfo("ru-RU").TextInfo.ToTitleCase(date.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU")))
        : date.ToString("dd.MM.yyyy") : "Все даты";
    // Keep local calendar boundaries; the existing query converts them to UTC once.
    internal DateTime? PeriodUntil => FilterDate is not { } date ? null : IsMonthPeriod
        ? date.Year == 9999 && date.Month == 12 ? null : date.AddMonths(1)
        : date == DateTime.MaxValue.Date ? null : date.AddDays(1);
    public void SelectMonth(DateTime date) => SetPeriod(new(date.Year, date.Month, 1), true);
    private void SetPeriod(DateTime? date, bool month)
    {
        month &= date is not null;
        if (_filterDate == date && _isMonthPeriod == month) return;
        _filterDate = date; _isMonthPeriod = month;
        OnPropertyChanged(nameof(FilterDate));
        OnPropertyChanged(nameof(IsMonthPeriod));
        OnPropertyChanged(nameof(PeriodCaption));
        OnPropertyChanged(nameof(IsTodaySelected));
        OnPropertyChanged(nameof(IsAllDatesSelected));
        if (!_refreshing)
        {
            if (_store is Infrastructure.IWorkflowQueryStore) ScheduleDatabaseSearch(); else RefreshOrders();
            // No card was open: leave rows unselected so the first click opens one.
            if (!IsOrderOpen) SelectedOrder = null;
        }
    }
    public bool IsTodaySelected
    {
        get => !IsMonthPeriod && FilterDate == DateTime.Today;
        set { if (value) FilterDate = DateTime.Today; }
    }
    public bool IsAllDatesSelected
    {
        get => FilterDate is null;
        set { if (value) FilterDate = null; }
    }
    public bool IsOrderListEmpty => _hasLoadedOrders && !_readingDatabase && !_searchPending && (!IsBusy || _savingProjectInclusion) && Orders.Count == 0;
    public string EmptyOrderListText => HasOrderFilters ? "По выбранным условиям заявок нет" : Search.Trim().Length > 0 ? "Заявки не найдены" :
        FilterDate is { } date ? "Нет заявок за " + (IsMonthPeriod ? date.ToString("MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU")) : date.ToString("dd.MM.yyyy")) : "Заявок пока нет";

    [RelayCommand] private void Today() => FilterDate = DateTime.Today;
    [RelayCommand] private void AllDates() => FilterDate = null;

    private bool IsInPeriod(Core.WorkflowOrder order)
    {
        if (FilterDate is not { } date) return true;
        var created = order.CreatedAt.ToLocalTime();
        return IsMonthPeriod ? created.Year == date.Year && created.Month == date.Month : created.Date == date;
    }
    private void NotifyOrderList()
    {
        NotifyNavigation();
        OnPropertyChanged(nameof(IsOrderListEmpty));
        OnPropertyChanged(nameof(EmptyOrderListText));
        OnPropertyChanged(nameof(SearchSummary));
    }
}
