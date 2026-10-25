using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private bool CanMaintainProject()
    {
        if (!CanUse || !IsCurrentOrderActive || CurrentOrder is not { IsDeleted: false }) return false;
        if (HasCurrentCardEdits || IsCurrentCardStale)
        { Error = "Сохраните или отмените черновик и обновите заявку перед изменением проектов."; return false; }
        return true;
    }

    public WorkflowProjectEditorViewModel? CreateProjectEditor(Guid projectId)
    {
        if (!CanMaintainProject() || CurrentOrder is not { } order) return null;
        try
        {
            var project = order.Projects.Single(p => p.Id == projectId);
            if (project.IsDeleted || project.NativePath is null || order.Specifications.Any(s => s.Id == project.Id))
                throw new WorkflowValidationException("Изменение доступно только для пустой заготовки без принятой спецификации.");
            Error = null;
            return new(_store, _workspace, order.Id, project.Id, ActorId(), ApplyProjectSnapshot);
        }
        catch (WorkflowValidationException ex) { Error = ex.Message; return null; }
    }

    private void ApplyProjectSnapshot(WorkflowWorkspace book)
    {
        var id = _openOrderId; _workspace = book; RefreshOrders();
        if (id is { } orderId && book.Orders.SingleOrDefault(o => o.Id == orderId) is { } order) LoadOrder(order);
        OnPropertyChanged(nameof(HasPendingEdits));
    }

    public async Task SetProjectTrashAsync(Guid projectId, bool deleted)
    {
        if (!CanMaintainProject() || CurrentOrder is not { } order) return;
        await RunAsync(async () =>
        {
            var expected = CardVersion; var actor = ActorId();
            var book = await _store.WriteOrderAsync(order.Id, expected, w =>
                WorkflowProjects.SetTrash(w, order.Id, expected, projectId, deleted, actor, DateTimeOffset.UtcNow));
            if (!deleted) ShowTrashedProjects = false;
            ApplyProjectSnapshot(book); RefreshProjects(deleted ? null : projectId); MarkResultStale();
        });
    }

}
