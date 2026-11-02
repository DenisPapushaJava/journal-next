using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowCloseRequest(IWorkflowWorkspaceStore Source, Guid? OrderId, long Revision,
    long WorkspaceVersion, bool Application, bool NeedsConfirmation, bool CanSave, string Title, string Message);

public partial class WorkflowViewModel
{
    private long _closeRevision;
    private bool _cardClosedManually;
    private bool _orderQueryFailed;
    private bool HasManualResultText => !string.IsNullOrWhiteSpace(ResultText) && ResultText != _generatedResultText;
    private static bool HasManualResultTextIn(CardState state) => !string.IsNullOrWhiteSpace(state.Result) && state.Result != state.GeneratedResult;
    private bool HasCurrentReviewCloseWork => CurrentReportReview?.Candidates.Any(c => !c.WasAccepted && c.Draft?.IsDirty == true) == true;
    private bool HasCurrentCloseWork => HasCurrentCardEdits || !IsCreating &&
        (HasManualResultText || HasCuttingTaskTextEdits || HasCurrentReviewCloseWork);
    private static bool HasCloseWorkIn(CardState state) => state.HasEdits || HasManualResultTextIn(state) || state.TaskEdit is not null;
    public bool HasUnfinishedTaskText => HasCuttingTaskTextEdits || _cards.Any(p => p.Key != _openOrderId && p.Value.TaskEdit is not null);
    public bool CanCloseWorkspace => CanUse && !IsCopyingCuttingTask && !_isCopyingResult && !_searchPending;
    public bool CanCloseCard => CanCloseWorkspace && (IsCardVisible || IsCreating);
    public string CloseCardHint => CanCloseCard ? "Закрыть карточку" : "Дождитесь завершения операции";
    public bool IsCardOutsideFilter => IsCardVisible && IsCurrentOrderActive && !HasCardReview &&
        !_readingDatabase && !_searchPending && !_orderQueryFailed && !Orders.Any(row => row.Order.Id == _openOrderId);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ManagerName) or nameof(Technologist) or nameof(Blcb) or nameof(ResultText) or
            nameof(NewCustomer) or nameof(NewManager) or nameof(IsCreating) or nameof(IsPlanning) or nameof(PlanMachine) or nameof(PlanThickness))
            ++_closeRevision;
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(ResultText)) NotifyCurrentDraftIndicator();
    }

    private bool HasWorkForOrder(Guid id) => id == _openOrderId && !IsCreating ? HasCurrentCloseWork :
        _cards.TryGetValue(id, out var state) && HasCloseWorkIn(state) || _reportReview?.OrderId == id && HasPendingReportEdits;

    private void NotifyCurrentDraftIndicator()
    {
        if (!IsCreating && _openOrderId is { } id)
            Orders.FirstOrDefault(row => row.Order.Id == id)?.ApplyDraftState(HasCurrentCloseWork);
    }

    private void NotifyNavigation()
    {
        NotifyCurrentDraftIndicator();
        OnPropertyChanged(nameof(CanCloseCard)); OnPropertyChanged(nameof(CloseCardHint));
        OnPropertyChanged(nameof(IsCardOutsideFilter)); OnPropertyChanged(nameof(HasUnfinishedTaskText));
    }

    [RelayCommand]
    private void ResetOrderFilters()
    {
        _refreshing = true;
        try { Search = ""; FilterDate = null; ResetQuickOrderFilterValues(); }
        finally { _refreshing = false; }
        if (_store is IWorkflowQueryStore) ScheduleDatabaseSearch(); else RefreshOrders(preserveRows: true);
    }

    internal WorkflowCloseRequest? PrepareClose(bool application)
    {
        if (!CanCloseWorkspace || !application && !CanCloseCard) return null;
        CaptureCard();
        var other = application ? _cards.Where(p => (p.Key != _openOrderId || IsCreating) && HasCloseWorkIn(p.Value)).ToArray() : [];
        var pendingReports = application ? HasPendingReportEdits : !IsCreating && HasCurrentReviewCloseWork;
        var needsConfirmation = HasCurrentCloseWork || other.Length > 0 || pendingReports || application && _workspace.Orders.Any(o => o.HasProjectRename);
        var save = !IsCreating && HasCurrentCardEdits && !IsPlanning && _preparedProject is null &&
            !HasManualResultText && !HasCuttingTaskTextEdits && !pendingReports && other.Length == 0 && CanSaveCloseData();
        var message = IsCreating && HasCurrentCardEdits ? "В новой заявке остались введённые данные." :
            !IsCreating && HasCurrentCloseWork ? OrderTitle + ": есть несохранённые изменения." : "Есть незавершённая работа в других заявках.";
        if (other.Length > 0) message += "\nДругих заявок с черновиками: " + other.Length + ".";
        if (!IsCreating && HasManualResultText || other.Any(p => HasManualResultTextIn(p.Value)))
            message += "\nРучной текст результата будет потерян при закрытии. Перед закрытием его можно скопировать.";
        if (!IsCreating && HasCuttingTaskTextEdits || other.Any(p => p.Value.TaskEdit is not null))
            message += "\nРучной текст задания в цех будет потерян. Перед закрытием его можно скопировать.";
        if (IsPlanning) message += "\nСоздание проекта при закрытии не выполняется.";
        if (_preparedProject?.NativePath is { } path) message += "\nПодготовленный файл останется на диске: " + path;
        if (pendingReports) message += "\nВ подборе спецификаций остались непринятые правки.";
        if (needsConfirmation && !save) message += "\nВернитесь к работе, чтобы сохранить данные или скопировать нужный текст.";
        return new(_store, _openOrderId, _closeRevision, _workspace.Version, application, needsConfirmation, save,
            application ? "Закрыть программу?" : "Закрыть карточку?", message);
    }

    private bool CanSaveCloseData()
    {
        if (CardWriteProblem is not null || CurrentOrder?.HasProjectRename == true || HasHeaderChanges && !CanSaveHeader) return false;
        try
        {
            foreach (var draft in Drafts.Where(d => d.IsDirty))
            {
                if (!draft.CanSave || string.IsNullOrWhiteSpace(draft.Number) || draft.DocumentDate() == default) return false;
                draft.Parameters(); draft.Rate();
            }
            return true;
        }
        catch (WorkflowValidationException) { return false; }
    }

    internal bool IsCloseRequestCurrent(WorkflowCloseRequest request) => CanCloseWorkspace &&
        ReferenceEquals(request.Source, _store) && request.OrderId == _openOrderId && request.Revision == _closeRevision &&
        request.WorkspaceVersion == _workspace.Version;

    internal async Task<bool> SaveCloseDataAsync(WorkflowCloseRequest request)
    {
        if (!IsCloseRequestCurrent(request) || !request.CanSave || !CanSaveCloseData()) return false;
        if (HasHeaderChanges)
        {
            await SaveHeaderCommand.ExecuteAsync(null);
            if (Error is not null || HasHeaderChanges) return false;
        }
        foreach (var draft in Drafts.Where(d => d.IsDirty).ToArray())
        {
            SelectedDraft = draft;
            await SaveSpecificationCommand.ExecuteAsync(null);
            if (Error is not null || Drafts.Any(d => d.Id == draft.Id && d.IsDirty)) return false;
        }
        return request.OrderId == _openOrderId && ReferenceEquals(request.Source, _store) && !HasCurrentCloseWork;
    }

    internal bool CloseCard(WorkflowCloseRequest request)
    {
        if (request.Application || !IsCloseRequestCurrent(request)) return false;
        var id = _openOrderId;
        if (IsCreating)
        {
            NewCustomer = ""; NewManager = ""; IsCreating = false;
            if (CurrentOrder is { } previous && _cards.TryGetValue(previous.Id, out var state)) RestoreCard(previous, state);
            else { ForgetOpenCard(); SelectedOrder = null; }
        }
        else
        {
            if (id is { } closed) _cards.Remove(closed);
            if (_reportReview?.OrderId == id) _reportReview = null;
            _cuttingTaskEdit = null; ForgetOpenCard();
            _refreshing = true;
            try { SelectedOrder = null; } finally { _refreshing = false; }
        }
        _cardClosedManually = !IsOrderOpen;
        Error = null; RefreshOrders(preserveRows: true);
        // Closing is deliberate: do not select or open the first row on refresh.
        if (!IsOrderOpen) { _refreshing = true; try { SelectedOrder = null; } finally { _refreshing = false; } }
        NotifyOrder(); return true;
    }
}
