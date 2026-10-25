using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private sealed record PlanState(bool IsOpen, Guid? MachineId, string Thickness,
        Guid? RedoItem, CuttingProject? Prepared);
    private sealed record CardState(long Version, bool HasEdits, SpecificationDraft[] Drafts, Guid? SelectedDraftId,
        Guid? SelectedProjectId, string ManagerName, Guid? TechnologistId, string? Blcb,
        string Result, bool ResultExpanded, bool ResultStale, PlanState Plan, bool LetterOnly = false, int TabIndex = 0,
        WorkflowOrder? SourceOrder = null);
    private readonly Dictionary<Guid, CardState> _cards = [];
    private bool _selectingCard;
    public bool IsCardVisible => IsOrderOpen && !IsCreating;
    public bool IsNoCardSelected => !IsOrderOpen && !IsCreating;
    public string HeaderSummary => CurrentOrder is not { } order ? "" :
        $"Менеджер: {(string.IsNullOrWhiteSpace(ManagerName) ? "не указан" : ManagerName)} · БЛЦБ: " +
        (string.IsNullOrWhiteSpace(Blcb) ? "пока нет" : Blcb);
    private void NotifyHeaderEdit() { OnPropertyChanged(nameof(HasPendingEdits)); OnPropertyChanged(nameof(HeaderSummary)); }
    private long CardVersion => _loadedOrderVersion ?? CurrentOrder?.Version ?? 0;
    private bool HasCurrentCardEdits => IsCreating ? !string.IsNullOrWhiteSpace(NewCustomer) || !string.IsNullOrWhiteSpace(NewManager) :
        IsPlanning || Drafts.Any(d => d.IsDirty) || CurrentOrder is { } o &&
        (!ManagerName.Trim().Equals(EmployeeName(o.ManagerId), StringComparison.OrdinalIgnoreCase) || Technologist?.Id != o.TechnologistId ||
            (string.IsNullOrWhiteSpace(Blcb) ? null : Blcb.Trim()) != o.Blcb);
    private bool HasDraftForOrder(Guid id) => id == _openOrderId && !IsCreating ? HasCurrentCardEdits :
        _cards.TryGetValue(id, out var state) && state.HasEdits;

    private void CaptureCard()
    {
        if (CurrentOrder is not { } order || IsCreating) return;
        _cards[order.Id] = new(CardVersion, HasCurrentCardEdits, Drafts.ToArray(), SelectedDraft?.Id,
            SelectedProject?.Project.Id, ManagerName, Technologist?.Id, Blcb,
            ResultText, IsResultExpanded, ResultIsStale, new(IsPlanning, PlanMachine?.Id,
                PlanThickness, _redoItem, _preparedProject), TabIndex: OrderTabIndex, SourceOrder: order);
    }
    private void SwitchCard(WorkflowOrder order)
    {
        if (_openOrderId == order.Id && !IsCreating) return;
        CaptureCard(); _cards.TryGetValue(order.Id, out var state);
        IsPlanning = false; _preparedProject = null; _redoItem = null;
        LoadOrder(order);
        if (state is not null) RestoreCard(order, state);
        RefreshOrders(); OnPropertyChanged(nameof(HasPendingEdits));
    }
    private void RestoreCard(WorkflowOrder order, CardState state)
    {
        // A dirty card keeps its original concurrency version. It cannot overwrite a newer saved order.
        if (!state.LetterOnly && (state.HasEdits || state.Version == order.Version))
        {
            Drafts.Clear(); foreach (var draft in state.Drafts) Drafts.Add(draft);
            ManagerName = state.ManagerName;
            Technologist = Employees.SingleOrDefault(e => e.Id == state.TechnologistId);
            Blcb = state.Blcb;
            PlanMachine = _workspace.Machines.SingleOrDefault(m => m.Id == state.Plan.MachineId);
            PlanThickness = state.Plan.Thickness;
            _redoItem = state.Plan.RedoItem; _preparedProject = state.Plan.Prepared; IsPlanning = state.Plan.IsOpen;
            _loadedOrderVersion = state.Version;
        }
        SelectedProject = Projects.SingleOrDefault(p => p.Project.Id == state.SelectedProjectId);
        SelectedDraft = Drafts.SingleOrDefault(d => d.Id == state.SelectedDraftId) ?? Drafts.FirstOrDefault();
        ResultText = state.Result; IsResultExpanded = state.ResultExpanded;
        OrderTabIndex = state.LetterOnly ? (state.ResultExpanded ? 1 : 0) : Math.Clamp(state.TabIndex, 0, 1);
        ResultIsStale = !string.IsNullOrWhiteSpace(state.Result) && (state.ResultStale || state.Version != order.Version);
        OnPropertyChanged(nameof(CanEditPlan)); OnPropertyChanged(nameof(PlanTitle));
        if (state.HasEdits && state.Version != order.Version)
            Error = "Заявка изменена в другом окне. Ваш черновик сохранён; перед записью нужно проверить изменения.";
        NotifyOrder();
    }
    partial void OnSelectedOrderChanged(WorkflowOrderRow? oldValue, WorkflowOrderRow? newValue)
    {
        if (_refreshing || _selectingCard) return;
        if (_savingProjectInclusion)
        {
            _selectingCard = true;
            try { SelectedOrder = oldValue; } finally { _selectingCard = false; }
            return;
        }
        if (!CanUse || newValue is null) return;
        if (IsCreating && HasCurrentCardEdits)
        {
            _selectingCard = true;
            try { SelectedOrder = oldValue; }
            finally { _selectingCard = false; }
            Error = "Сначала создайте или отмените новую заявку."; return;
        }
        if (IsCreating) CancelNewOrder();
        SwitchCard(newValue.Order);
    }
}
