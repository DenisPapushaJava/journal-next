using Avalonia.Controls;
using Avalonia.Interactivity;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private async void OnCalculationHistory(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0 || sender is not Control { DataContext: WorkflowProjectRow { CanViewHistory: true } row }) return;
        try
        {
            var history = await model.CreateCalculationHistoryAsync(row.Project.Id, _lifetime.Token);
            if (history is not null && IsVisible && ReferenceEquals(Model, model) && !_lifetime.IsCancellationRequested)
                await new WorkflowCalculationHistoryWindow { DataContext = history, RequestedThemeVariant = ActualThemeVariant }.ShowDialog(this);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsVisible) model.Error = "Не удалось открыть историю расчёта. Проверьте доступ к папке проекта."; }
    }
}

public partial class WorkflowViewModel
{
    public async Task<WorkflowCalculationHistoryViewModel?> CreateCalculationHistoryAsync(Guid projectId, CancellationToken token = default)
    {
        if (!CanUse || CurrentOrder is not { } order) return null;
        var source = _store; WorkflowCalculationHistoryViewModel? result = null;
        await RunAsync(async () =>
        {
            var workspace = await source.ReadOrderAsync(order.Id, token);
            var current = workspace.Orders.SingleOrDefault(o => o.Id == order.Id);
            var project = current?.Projects.SingleOrDefault(p => p.Id == projectId);
            var saved = current?.Specifications.SingleOrDefault(s => s.Id == projectId)?.Current;
            if (saved is null || project?.NativePath is not { } native) return;
            var history = await Task.Run(() => WorkflowCalculationHistory.ReadAsync(Path.GetDirectoryName(native)!, order.Id, projectId, token), token);
            if (!ReferenceEquals(source, _store) || CurrentOrder?.Id != order.Id || _cuttingTaskDetached) return;
            result = new(project.Name, saved, history, workspace.Employees, current!.Version);
        }, "Не удалось прочитать историю. Проверьте доступ к папке проекта.");
        return result;
    }
}
