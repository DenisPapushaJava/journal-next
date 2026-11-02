using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public enum CalculationTarget { None, Header, Project, Reports, Recalculate, Refresh, Settings, Folder }

public partial class WorkflowViewModel
{
    private bool _calculatingOrder;
    internal bool CanRetryCalculation { get; private set; }
    internal void ExplainCalculationRetry() => SetCalculationNotice("Заявка сейчас обновляется. Повторите «Рассчитать» — введённые данные сохранены.", CalculationTarget.Recalculate);
    private (IWorkflowWorkspaceStore Source, Guid OrderId, string Text, CalculationTarget Target, Guid? ProjectId)? _calculationNotice;
    public bool CanCalculateOrder => CanUse && IsCurrentOrderActive && !IsCreating && !_calculatingOrder && CurrentOrder is { } current && (current.ActiveProjects.Any() || current.ActiveSpecifications.Any());
    public string CalculateOrderHint => CurrentOrder?.ActiveProjects.Any() != true && CurrentOrder?.ActiveSpecifications.Any() != true ? "Сначала добавьте проект в заявку." : "Найти спецификации, обновить расчёты включённых проектов и текст результата";
    private bool IsCalculationNoticeCurrent => _calculationNotice is { } notice && ReferenceEquals(notice.Source, _store) && notice.OrderId == CurrentOrder?.Id;
    public string CalculationNotice => IsCalculationNoticeCurrent ? _calculationNotice!.Value.Text : "";
    public bool HasCalculationNotice => CalculationNotice.Length > 0;
    public bool HasCalculationNextStep => IsCalculationNoticeCurrent && _calculationNotice!.Value.Target != CalculationTarget.None;
    public string CalculationActionCaption => IsCalculationNoticeCurrent ? _calculationNotice!.Value.Target switch
    {
        CalculationTarget.Header => "Открыть реквизиты", CalculationTarget.Project => "Перейти к проекту",
        CalculationTarget.Reports => "Уточнить расчёт", CalculationTarget.Recalculate => "Повторить расчёт", CalculationTarget.Refresh => "Обновить журнал",
        CalculationTarget.Settings => "Открыть настройки", CalculationTarget.Folder => "Открыть папку проекта", _ => ""
    } : "";

    private HashSet<Guid> CalculationProjectIds(WorkflowOrder order) => order.Items.Where(i => i.IsIncluded)
        .Select(i => i.CurrentProjectId).OfType<Guid>()
        .Concat(order.ActiveSpecifications.Where(s => !order.Projects.Any(p => p.Id == s.Id)).Select(s => s.Id)).ToHashSet();

    private bool CheckCalculationInputs()
    {
        if (CurrentOrder is not { } order) return false;
        if (RequiresDatabaseRefresh) { SetCalculationNotice("Обновите журнал, чтобы проверить сохранение предыдущего расчёта.", CalculationTarget.Refresh); return false; }
        if (AuthorProblem is { } author) { SetCalculationNotice(author, CalculationTarget.Settings); return false; }
        if (HasHeaderChanges && (_loadedOrderSnapshot is not { } original ||
            ManagerName.Trim() != EmployeeName(original.ManagerId) || Technologist?.Id != original.TechnologistId ||
            (string.IsNullOrWhiteSpace(Blcb) ? null : Blcb.Trim()) != original.Blcb)) { SetCalculationNotice("Сохраните или отмените изменения реквизитов.", CalculationTarget.Header); return false; }
        if (IsPlanning) { SetCalculationNotice("Создайте проект или отмените ввод его параметров.", CalculationTarget.Project); return false; }
        if (order.HasProjectRename) { SetCalculationNotice("Завершите изменение параметров проекта.", CalculationTarget.Project, order.Projects.First(p => p.Rename is not null).Id); return false; }
        if (HasUnselectedVariant) { SetCalculationNotice("Выберите актуальный вариант включённого проекта.", CalculationTarget.Project,
            order.ActiveProjects.FirstOrDefault(p => order.Items.Any(i => i.Id == p.ItemId && i.IsIncluded && p.Variant > i.ReviewedThroughVariant))?.Id); return false; }
        var ids = CalculationProjectIds(order);
        if (ids.Count == 0) { SetCalculationNotice("Добавьте или включите проект в состав заявки.", CalculationTarget.Project); return false; }
        return true;
    }

    private bool CheckPendingCalculationReview()
    {
        if (_reportReview is { } session && session.OrderId != CurrentOrder?.Id && HasPendingReportEdits)
        {
            var caption = _workspace.Orders.SingleOrDefault(o => o.Id == session.OrderId)?.DisplayNumber ?? session.OrderId.ToString();
            SetCalculationNotice($"В заявке {caption} остались непринятые правки спецификаций. Завершите их перед новым подбором.", CalculationTarget.Reports);
            return false;
        }
        return true;
    }

    private void SetCalculationNotice(string text, CalculationTarget target = CalculationTarget.None, Guid? projectId = null)
    {
        if (CurrentOrder is not { } order) return;
        _calculationNotice = (_store, order.Id, text, target, projectId); NotifyCalculationState();
    }
    private void NotifyCalculationState()
    {
        foreach (var name in new[] { nameof(CanCalculateOrder), nameof(CalculateOrderHint), nameof(CalculationNotice),
            nameof(HasCalculationNotice), nameof(HasCalculationNextStep), nameof(CalculationActionCaption) }) OnPropertyChanged(name);
    }
    public CalculationTarget GoToCalculationProblem()
    {
        if (!CanUse || !IsCalculationNoticeCurrent || _calculationNotice is not { } notice) return CalculationTarget.None;
        if (notice.Target == CalculationTarget.Reports && _reportReview is { } session && session.OrderId != CurrentOrder?.Id &&
            _workspace.Orders.SingleOrDefault(o => o.Id == session.OrderId) is { } pendingOrder) SwitchCard(pendingOrder);
        if (notice.ProjectId is { } id)
        { OrderTabIndex = 0; ShowTrashedProjects = false; SelectedProject = Projects.SingleOrDefault(p => p.Project.Id == id); }
        return notice.Target;
    }

    // Automatic acceptance uses the same validation, archive and conditional write as manual review.
    public async Task<WorkflowReportReviewViewModel?> CalculateOrderAsync(CancellationToken token = default)
    {
        if (!CanCalculateOrder) return null;
        _acceptedReports = 0; _reportImportOrderId = null; NotifyCompleteness();
        if (!CheckCalculationInputs()) return null;
        CanRetryCalculation = false;
        _calculatingOrder = true; NotifyCalculationState();
        var source = _store; var id = CurrentOrder!.Id;
        try
        {
            SetCalculationNotice("Ищем спецификации и рассчитываем проекты…");
            token.ThrowIfCancellationRequested();
            var review = CurrentOrder!.Projects.Length == 0 ? new WorkflowReportReviewViewModel((_, _) => Task.FromResult<string?>("У расчёта нет рабочего проекта."))
                { PreviewOrderVersion = CurrentOrder.Version, IsContextCurrent = () => ReferenceEquals(source, _store) && CurrentOrder?.Id == id && !_cuttingTaskDetached }
                : await CreateReportReviewAsync(token, calculation: true);
            token.ThrowIfCancellationRequested();
            if (review is null) { if (Error is { } failure) SetCalculationNotice(failure, RequiresDatabaseRefresh ? CalculationTarget.Refresh : CalculationTarget.Recalculate); return null; }
            if (_cuttingTaskDetached || !ReferenceEquals(source, _store) || CurrentOrder?.Id != id) return null;
            review.IsGuided = true;
            await review.AcceptAutomaticAsync(token);
            if (review.Error is { } error) { SetCalculationNotice(error, RequiresDatabaseRefresh ? CalculationTarget.Refresh : CalculationTarget.Recalculate); return review; }
            CompleteCalculation(review);
            return review;
        }
        catch (OperationCanceledException) { SetCalculationNotice("Расчёт отменён. Сохранённые расчёты и ручные правки сохранены."); return null; }
        finally { _calculatingOrder = false; NotifyCalculationState(); }
    }

    public async Task<bool> FinalizeCalculationAsync(WorkflowReportReviewViewModel review, CancellationToken token = default)
    {
        if (!CompleteCalculation(review) || CurrentOrder is not { } order) return false;
        var source = _store; var ready = false;
        await RunAsync(async () =>
        {
            var fresh = await source.ReadOrderAsync(order.Id, token);
            if (review.IsContextCurrent?.Invoke() != true) return;
            if (fresh.Orders.SingleOrDefault(o => o.Id == order.Id) is not { IsDeleted: false } current || current.Version != review.PreviewOrderVersion)
            {
                CanRetryCalculation = true; MarkResultStale();
                SetCalculationNotice("Данные заявки обновились. Пересчитаем по актуальной спецификации.", CalculationTarget.Recalculate);
                return;
            }
            foreach (var candidate in review.Candidates.Where(c => c.Snapshot is not null))
            {
                try { await Task.Run(() => WorkflowReportFiles.RevalidateAsync(candidate.Snapshot!, token), token); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
                {
                    CanRetryCalculation = ex is WorkflowValidationException;
                    candidate.MarkSourceChanged("Спецификация изменилась или недоступна. Завершите сохранение в CAM и повторите расчёт.");
                    _reportReview = (order.Id, review); MarkResultStale(); NotifyCompleteness();
                    SetCalculationNotice($"Проект «{candidate.Project.Name}»: {candidate.Message}", CalculationTarget.Recalculate, candidate.Project.Id);
                    return;
                }
            }
            ready = review.IsContextCurrent?.Invoke() == true;
        });
        if (!ready && Error is { } error) SetCalculationNotice(error, CalculationTarget.Refresh);
        return ready;
    }

    public bool CompleteCalculation(WorkflowReportReviewViewModel review)
    {
        if (review.IsContextCurrent?.Invoke() != true || review.PreviewOrderVersion != CurrentOrder?.Version || review.Error is not null) return false;
        if (review.GuidedCandidates.FirstOrDefault() is { } question)
        { review.Selected = question; SetCalculationNotice($"Проект «{question.Project.Name}»: {question.Message}", CalculationTarget.Reports, question.Project.Id); return false; }
        if (review.Candidates.FirstOrDefault(c => !c.WasAccepted && c.Status != "Без изменений") is { } missing)
        { SetCalculationNotice($"{CompletenessCaption}. Проект «{missing.Project.Name}»: {missing.Message}. Сохраните спецификацию рядом с проектом и нажмите «Рассчитать».", CalculationTarget.Folder, missing.Project.Id); return false; }
        if (ResultGenerationProblem is { } problem) { SetCalculationNotice(problem, CalculationTarget.Project); return false; }
        SetCalculationNotice("");
        return true;
    }
}
