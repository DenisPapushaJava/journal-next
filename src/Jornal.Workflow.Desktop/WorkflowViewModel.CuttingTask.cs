using CommunityToolkit.Mvvm.ComponentModel;
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
    [ObservableProperty] private bool _isCopyingCuttingTask;
    public bool CanCopyCuttingTask => CanUse && IsCurrentOrderActive && !IsCopyingCuttingTask;
    public bool IsCuttingTaskVisible => ReferenceEquals(_cuttingTaskSource, _store) && CurrentOrder?.Id == _cuttingTaskOrderId;
    public string CuttingTaskText => TaskPreview.Text;
    public bool HasCuttingTaskText => TaskPreview.IsReady;
    public bool IsCuttingTaskReadyVisible => IsCuttingTaskVisible && HasCuttingTaskText && ShopTransfer is null;
    public bool IsCuttingTaskProblemVisible => IsCuttingTaskVisible && !HasCuttingTaskText;
    public bool CanGoToCuttingTaskProblem => TaskPreview.Problem is { Target: not CuttingTaskTarget.None };
    public string CuttingTaskNotice => TaskPreview.Problem?.Message ?? _cuttingTaskCopyError ??
        (_copiedCuttingTask is { } copied && copied.OrderId == CurrentOrder?.Id && copied.Version == CurrentOrder.Version && copied.Text == TaskPreview.Text ?
            "Задание скопировано" : "Текст задания готов");
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
        if (!ManagerName.Trim().Equals(EmployeeName(order.ManagerId), StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(Blcb) ? null : Blcb.Trim()) != order.Blcb || Technologist?.Id != order.TechnologistId)
            return Block("Сохраните или отмените изменения реквизитов.", CuttingTaskTarget.Header);
        if (Drafts.FirstOrDefault(d => d.IsDirty) is { } draft)
            return Block("Сохраните или отмените изменения расчёта.", CuttingTaskTarget.Project, draft.Id);
        if (CurrentReportReview?.Candidates.FirstOrDefault(c => c.NeedsAttention) is { } candidate)
            return Block("Проверьте найденную спецификацию перед подготовкой задания.", CuttingTaskTarget.Reports, candidate.Project.Id);
        return WorkflowCuttingTask.Prepare(order, _workspace.Employees, _workspace.Customers, _workspace.Machines);
    }
    private void NotifyCuttingTask()
    {
        _cuttingTaskPreview = null;
        foreach (var name in new[] { nameof(CanCopyCuttingTask), nameof(IsCuttingTaskVisible), nameof(CuttingTaskText), nameof(HasCuttingTaskText),
            nameof(IsCuttingTaskReadyVisible), nameof(IsCuttingTaskProblemVisible), nameof(CanGoToCuttingTaskProblem), nameof(CuttingTaskNotice), nameof(CuttingTaskActionCaption), nameof(HasShopTransfer) }) OnPropertyChanged(name);
    }
    private void ResetCuttingTask()
    {
        ResetShopTransfer();
        _cuttingTaskEpoch++; _cuttingTaskSource = null; _cuttingTaskOrderId = null; _copiedCuttingTask = null; _cuttingTaskCopyError = null;
        NotifyCuttingTask();
    }
    partial void OnIsCopyingCuttingTaskChanged(bool value) => OnPropertyChanged(nameof(CanCopyCuttingTask));

    public async Task CopyCuttingTaskAsync(Func<string, Task> writeClipboard)
    {
        if (!CanCopyCuttingTask || CurrentOrder is not { } order) return;
        _cuttingTaskSource = _store; _cuttingTaskOrderId = order.Id; _cuttingTaskCopyError = null; _copiedCuttingTask = null;
        NotifyCuttingTask();
        if (!TaskPreview.IsReady) return;
        var text = TaskPreview.Text; var source = _store; var version = order.Version; var epoch = _cuttingTaskEpoch;
        IsCopyingCuttingTask = true;
        bool StillCurrent() => !_cuttingTaskDetached && epoch == _cuttingTaskEpoch && ReferenceEquals(source, _store) && CurrentOrder?.Id == order.Id &&
            CurrentOrder.Version == version && PrepareCuttingTask() is { IsReady: true } current && current.Text == text;
        try
        {
            await writeClipboard(text);
            if (StillCurrent()) _copiedCuttingTask = (order.Id, version, text);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (StillCurrent()) _cuttingTaskCopyError = "Не удалось скопировать задание. Повторите копирование.";
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
