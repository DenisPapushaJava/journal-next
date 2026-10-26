using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowInclusionWrite(Guid OrderId, long Version, Guid ItemId, bool Included, Guid ActorId, DateTimeOffset At);

public partial class WorkflowViewModel
{
    private bool _savingProjectInclusion;
    private readonly Func<WorkflowInclusionWrite, Task<WorkflowWorkspace>>? _inclusionWriter;

    internal WorkflowViewModel(IWorkflowWorkspaceStore store, AppUpdatesViewModel updates,
        Func<WorkflowInclusionWrite, Task<WorkflowWorkspace>> inclusionWriter) : this(store, updates) => _inclusionWriter = inclusionWriter;

    public async Task SetProjectIncludedAsync(Guid projectId, bool included)
    {
        if (!CanMaintainProject() || CurrentOrder is not { } order) return;
        var project = order.Projects.SingleOrDefault(p => p.Id == projectId);
        if (project is null) { Error = "Проект не найден. Обновите заявку."; return; }
        if (project.IsDeleted) { Error = "Сначала восстановите проект из корзины."; return; }
        if (order.Items.Single(i => i.Id == project.ItemId).IsIncluded == included) return;
        _savingProjectInclusion = true;
        foreach (var row in Projects) row.SetInclusionWritePending(true, row.Project.ItemId == project.ItemId ? included : null);
        NotifyInclusionBusy();
        try
        {
            await RunAsync(async () =>
            {
                var request = new WorkflowInclusionWrite(order.Id, CardVersion, project.ItemId, included, ActorId(), DateTimeOffset.UtcNow);
                var book = await (_inclusionWriter?.Invoke(request) ?? WriteInclusionAsync(request));
                ApplyInclusionSnapshot(book, order.Id);
            }, "Не удалось подтвердить изменение состава. Обновите журнал перед повтором; автоматического повтора нет.", projectInclusion: true);
        }
        finally
        {
            _savingProjectInclusion = false;
            foreach (var row in Projects) row.SetInclusionWritePending(false);
            NotifyInclusionBusy();
        }
    }

    private Task<WorkflowWorkspace> WriteInclusionAsync(WorkflowInclusionWrite request) =>
        _store.WriteOrderAsync(request.OrderId, request.Version, w => WorkflowProjects.SetIncluded(w, request.OrderId,
            request.Version, request.ItemId, request.Included, request.ActorId, request.At));

    private void NotifyInclusionBusy()
    {
        OnPropertyChanged(nameof(CanUse)); OnPropertyChanged(nameof(CanBrowseWorkspace));
        SaveSpecificationCommand.NotifyCanExecuteChanged(); NotifyOrderList();
        NotifyCuttingTask();
    }

    private void ApplyInclusionSnapshot(WorkflowWorkspace book, Guid orderId)
    {
        var oldEmployee = CurrentEmployee is { } actor ? _workspace.Employees.SingleOrDefault(e => e.Id == actor.Id) : null;
        _workspace = book;
        if (_openOrderId == orderId)
        {
            var order = book.Orders.Single(o => o.Id == orderId);
            _loadedOrderVersion = order.Version; _loadedOrderSnapshot = order;
            var inclusion = order.Items.ToDictionary(i => i.Id, i => i.IsIncluded);
            foreach (var row in Projects.Where(p => !p.IsLegacyCalculation)) row.ApplyIncluded(inclusion[row.Project.ItemId]);
            // Specifications have not changed. Keep their objects, selection and local input.
            MarkResultStale(); NotifyOrder();
        }
        RefreshOrders(preserveRows: true);
        var employee = CurrentEmployee is { } current ? book.Employees.SingleOrDefault(e => e.Id == current.Id) : null;
        if (oldEmployee != employee) NotifyProfile();
    }

    private void SynchronizeOrderRows(IReadOnlyList<WorkflowOrderRow> desired)
    {
        var existing = Orders.ToDictionary(r => r.Order.Id);
        for (var i = 0; i < desired.Count; i++)
        {
            if (!existing.TryGetValue(desired[i].Order.Id, out var row)) { Orders.Insert(i, desired[i]); continue; }
            row.Apply(desired[i]);
            var index = Orders.IndexOf(row);
            if (index != i) Orders.Move(index, i);
        }
        while (Orders.Count > desired.Count) Orders.RemoveAt(Orders.Count - 1);
    }
}
