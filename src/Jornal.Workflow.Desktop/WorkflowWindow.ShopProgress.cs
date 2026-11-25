namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private async Task<WorkflowShopTransferViewModel?> WithShopProgressAsync(Task<WorkflowShopTransferViewModel?> operation)
    {
        WorkflowShopProgressWindow? progress = null; Task? closed = null;
        try
        {
            if (IsVisible && !_lifetime.IsCancellationRequested && Model?.ShopTransfer is { } preview)
            {
                progress = new() { DataContext = preview, RequestedThemeVariant = ActualThemeVariant };
                closed = progress.ShowDialog(this);
            }
            var result = await operation;
            if (progress is not null) { progress.Finish(); if (closed is not null) await closed; }
            if (progress?.CalculationRequested == true && result?.OfficeCalculationProjectId is { } projectId &&
                IsVisible && Model is { CanCalculateOrder: true } model && ReferenceEquals(model.ShopTransfer, result))
            {
                model.OrderTabIndex = 0;
                model.ShowTrashedProjects = false;
                model.SelectedProject = model.Projects.SingleOrDefault(row => row.Project.Id == projectId);
                await CalculateFromWindowAsync(rebuildText: false);
                return null;
            }
            return result;
        }
        finally { progress?.Finish(forceClose: true); }
    }
}
