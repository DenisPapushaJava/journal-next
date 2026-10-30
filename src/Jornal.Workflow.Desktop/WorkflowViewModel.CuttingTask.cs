using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private CuttingTaskPreview? _cuttingTaskPreview;
    private IWorkflowWorkspaceStore? _cuttingTaskSource;
    private Guid? _cuttingTaskOrderId;
    private (Guid OrderId, long Version, string Text)? _copiedCuttingTask;
    private string? _cuttingTaskCopyError;
    private bool _cuttingTaskDetached;
    private long _cuttingTaskEpoch;
    private sealed record CuttingTaskEdit(IWorkflowWorkspaceStore Source, Guid OrderId, long Version, string Generated, string Text);
    private CuttingTaskEdit? _cuttingTaskEdit;
    [ObservableProperty] private bool _isCopyingCuttingTask;
    public bool CanCopyCuttingTask => CanUse && IsCurrentOrderActive && !IsCopyingCuttingTask && !_isCopyingResult;
    public bool CanSendToWork => CanCopyCuttingTask && !IsCreating && AuthorProblem is null && TaskPreview.IsReady && !string.IsNullOrWhiteSpace(CuttingTaskText);
    public string SendToWorkHint => !CanCopyCuttingTask ? "Дождитесь завершения операции или откройте активную заявку." : AuthorProblem ??
        TaskPreview.Problem?.Message ?? (string.IsNullOrWhiteSpace(CuttingTaskText) ? "Введите текст задания в цех." :
        ShopRoot is null ? "Выбрать папку цеха в настройках и подготовить задание" : "Проверить УП и карты, подготовить файлы и скопировать текст задания");
    public bool CanCopyCuttingTaskText => CanCopyCuttingTask && HasCuttingTaskText && !string.IsNullOrWhiteSpace(CuttingTaskText);
    public bool IsCuttingTaskVisible => ReferenceEquals(_cuttingTaskSource, _store) && CurrentOrder?.Id == _cuttingTaskOrderId;
    public string CuttingTaskText
    {
        get => _cuttingTaskEdit?.Text ?? TaskPreview.Text;
        set
        {
            if (!CanCopyCuttingTask || !TaskPreview.IsReady || CurrentOrder is not { } order || value == CuttingTaskText) return;
            _cuttingTaskEdit = value == TaskPreview.Text ? null : new(_store, order.Id, order.Version, TaskPreview.Text, value ?? "");
            _copiedCuttingTask = null; _cuttingTaskCopyError = null; NotifyCuttingTaskText();
        }
    }
    public bool HasCuttingTaskTextEdits => _cuttingTaskEdit is not null;
    public bool HasCuttingTaskText => TaskPreview.IsReady;
    private bool HasCurrentCuttingTaskCopy => _copiedCuttingTask is { } copied && copied.OrderId == CurrentOrder?.Id &&
        copied.Version == CurrentOrder.Version && copied.Text == CuttingTaskText;
    public bool HasCuttingTaskCopyFeedback => IsCuttingTaskVisible && HasCuttingTaskText && ShopTransfer is null &&
        (_cuttingTaskCopyError is not null || HasCurrentCuttingTaskCopy);
    public bool IsCuttingTaskProblemVisible => IsCuttingTaskVisible && !HasCuttingTaskText;
    public bool CanGoToCuttingTaskProblem => TaskPreview.Problem is { Target: not CuttingTaskTarget.None };
    public string CuttingTaskNotice => TaskPreview.Problem?.Message ?? _cuttingTaskCopyError ??
        (string.IsNullOrWhiteSpace(CuttingTaskText) ? "Введите текст задания." :
        HasCurrentCuttingTaskCopy ? "Задание скопировано" : HasCuttingTaskTextEdits ? "Текст задания изменён" : "Текст задания готов");
    public string CuttingTaskActionCaption => TaskPreview.Problem?.Target switch
    {
        CuttingTaskTarget.Header => "Открыть реквизиты", CuttingTaskTarget.Project => "Перейти к проекту",
        CuttingTaskTarget.Projects => "Открыть проекты", CuttingTaskTarget.Refresh => "Проверить изменения",
        CuttingTaskTarget.Reports => "Проверить спецификации", _ => ""
    };
    private CuttingTaskPreview TaskPreview => _cuttingTaskPreview ??= PrepareCuttingTask();
    private CuttingTaskPreview PrepareCuttingTask()
    {
        CuttingTaskPreview Block(string message, CuttingTaskTarget target, Guid? id = null) => new("", new(message, target, id));
        if (CurrentOrder is not { } order) return Block("Сначала откройте заявку.", CuttingTaskTarget.None);
        if (!IsCurrentOrderActive || IsCurrentCardStale || RequiresDatabaseRefresh)
            return Block("Проверьте изменения заявки и обновите журнал (F5).", CuttingTaskTarget.Refresh);
        if (IsPlanning) return Block("Создайте проект или отмените ввод его параметров.", CuttingTaskTarget.Projects);
        if (HasHeaderChanges)
            return Block("Сохраните или отмените изменения реквизитов.", CuttingTaskTarget.Header);
        if (Drafts.FirstOrDefault(d => d.IsDirty) is { } draft)
            return Block("Сохраните или отмените изменения расчёта.", CuttingTaskTarget.Project, draft.Id);
        if (CurrentReportReview?.Candidates.FirstOrDefault(c => c.NeedsAttention) is { } candidate)
            return Block("Проверьте найденную спецификацию перед подготовкой задания.", CuttingTaskTarget.Reports, candidate.Project.Id);
        return WorkflowCuttingTask.Prepare(order, _workspace.Employees, _workspace.Customers, _workspace.Machines);
    }
    private void NotifyCuttingTask()
    {
        NotifyMapSigning();
        _cuttingTaskPreview = null;
        if (_cuttingTaskEdit is { } edit && (!ReferenceEquals(edit.Source, _store) || CurrentOrder?.Id != edit.OrderId ||
            CurrentOrder.Version != edit.Version || TaskPreview.IsReady && TaskPreview.Text != edit.Generated))
        { _cuttingTaskEdit = null; _copiedCuttingTask = null; _cuttingTaskCopyError = null; }
        foreach (var name in new[] { nameof(CanCopyCuttingTask), nameof(CanCopyCuttingTaskText), nameof(IsCuttingTaskVisible), nameof(CuttingTaskText), nameof(HasCuttingTaskText),
            nameof(CanSendToWork), nameof(SendToWorkHint), nameof(HasCuttingTaskTextEdits), nameof(HasCuttingTaskCopyFeedback), nameof(IsCuttingTaskProblemVisible), nameof(CanGoToCuttingTaskProblem), nameof(CuttingTaskNotice), nameof(CuttingTaskActionCaption), nameof(HasShopTransfer) }) OnPropertyChanged(name);
        NotifyActionAvailability();
    }

    private void NotifyCuttingTaskText()
    {
        foreach (var name in new[] { nameof(CuttingTaskText), nameof(HasCuttingTaskTextEdits), nameof(CanCopyCuttingTaskText), nameof(CanSendToWork), nameof(SendToWorkHint),
            nameof(HasCuttingTaskCopyFeedback), nameof(CuttingTaskNotice) }) OnPropertyChanged(name);
    }

    [RelayCommand]
    private void ResetCuttingTaskText()
    {
        if (!CanCopyCuttingTask || !HasCuttingTaskText) return;
        _cuttingTaskEdit = null; _copiedCuttingTask = null; _cuttingTaskCopyError = null; NotifyCuttingTaskText();
    }
    private void ResetCuttingTask()
    {
        ResetShopTransfer();
        _cuttingTaskEpoch++; _cuttingTaskSource = null; _cuttingTaskOrderId = null; _copiedCuttingTask = null; _cuttingTaskCopyError = null;
        NotifyCuttingTask();
    }
    partial void OnIsCopyingCuttingTaskChanged(bool value)
    { OnPropertyChanged(nameof(CanCopyCuttingTask)); OnPropertyChanged(nameof(CanCopyCuttingTaskText)); OnPropertyChanged(nameof(CanSendToWork)); OnPropertyChanged(nameof(SendToWorkHint)); NotifyResultState(); }

    private bool IsCurrentCuttingTaskCopy(IWorkflowWorkspaceStore source, WorkflowOrder order, long epoch, string generated, string text) =>
        !_cuttingTaskDetached && epoch == _cuttingTaskEpoch && ReferenceEquals(source, _store) && CurrentOrder?.Id == order.Id &&
        CurrentOrder.Version == order.Version && PrepareCuttingTask() is { IsReady: true } current && current.Text == generated && CuttingTaskText == text;

    private void RememberCuttingTaskCopy(IWorkflowWorkspaceStore source, WorkflowOrder order, long epoch, string generated, string text)
    {
        if (!IsCurrentCuttingTaskCopy(source, order, epoch, generated, text)) return;
        _copiedCuttingTask = (order.Id, order.Version, text); _cuttingTaskCopyError = null;
        NotifyCuttingTask();
    }

    public async Task CopyCuttingTaskAsync(Func<string, Task> writeClipboard)
    {
        if (!CanCopyCuttingTask || CurrentOrder is not { } order) return;
        _cuttingTaskSource = _store; _cuttingTaskOrderId = order.Id; _cuttingTaskCopyError = null; _copiedCuttingTask = null;
        NotifyCuttingTask();
        if (!TaskPreview.IsReady || string.IsNullOrWhiteSpace(CuttingTaskText)) return;
        var generated = TaskPreview.Text; var text = CuttingTaskText; var source = _store; var epoch = _cuttingTaskEpoch;
        IsCopyingCuttingTask = true;
        try
        {
            await writeClipboard(text);
            RememberCuttingTaskCopy(source, order, epoch, generated, text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsCurrentCuttingTaskCopy(source, order, epoch, generated, text)) _cuttingTaskCopyError = "Не удалось скопировать задание. Повторите копирование.";
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
        finally { IsCopyingCuttingTask = false; NotifyCuttingTask(); }
    }

    public CuttingTaskTarget GoToCuttingTaskProblem()
    {
        if (!CanUse || TaskPreview.Problem is not { } problem) return CuttingTaskTarget.None;
        if (problem.Target is CuttingTaskTarget.Project or CuttingTaskTarget.Projects)
        {
            OrderTabIndex = 0; ShowTrashedProjects = false;
            if (problem.ProjectId is { } id) SelectedProject = Projects.SingleOrDefault(p => p.Project.Id == id);
        }
        return problem.Target;
    }
}
