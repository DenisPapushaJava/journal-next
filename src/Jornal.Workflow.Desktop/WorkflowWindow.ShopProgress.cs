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
            return result;
        }
        finally { progress?.Finish(forceClose: true); }
    }
}
