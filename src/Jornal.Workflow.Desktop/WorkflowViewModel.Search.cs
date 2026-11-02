using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private WorkflowOrderSearchIndex? _orderSearchIndex;
    private bool _isExactNumberSearch;
    public bool HasSearchText => Search.Length > 0;
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(Search);
    public string SearchSummary => _readingDatabase || _searchPending ? "Загрузка заявок…" : (_isExactNumberSearch ? "По номеру заявки: " : HasSearchQuery || HasOrderFilters ? "Найдено заявок: " : "Заявок: ") +
        (_store is Infrastructure.IWorkflowQueryStore query && query.HasMore ? $"{Orders.Count} из {Math.Max(Orders.Count, query.TotalCount)}" : Orders.Count.ToString()) + " · " +
        PeriodCaption;

    [RelayCommand] private void ClearSearch()
    {
        if (Search.Length == 0) return;
        Search = "";
    }
    private WorkflowOrderSearchIndex OrderSearchIndex =>
        _orderSearchIndex is { } index && ReferenceEquals(index.Source, _workspace) ? index : _orderSearchIndex = new(_workspace);
}
