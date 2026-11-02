using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private bool _calculatingReports;
    private async void OnCalculateOrder(object? sender, RoutedEventArgs e)
    {
        if (_calculatingReports || OwnedWindows.Count > 0 || Model is not { CanCalculateOrder: true } model) return;
        var rebuildText = ReferenceEquals(sender, GenerateWorkflowResult);
        _calculatingReports = true;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var review = await model.CalculateOrderAsync(_lifetime.Token);
                if (!IsVisible || !ReferenceEquals(Model, model) || review is null || _lifetime.IsCancellationRequested) return;
                if (model.CanRetryCalculation)
                {
                    if (attempt == 0) continue;
                    model.ExplainCalculationRetry(); return;
                }
                if (review.GuidedCandidates.Any())
                {
                    await ShowReportReviewAsync(model, review);
                    if (!IsVisible || !ReferenceEquals(Model, model) || _lifetime.IsCancellationRequested) return;
                }
                if (!await model.FinalizeCalculationAsync(review, _lifetime.Token))
                {
                    if (model.CanRetryCalculation && attempt == 0) continue;
                    if (model.CanRetryCalculation) model.ExplainCalculationRetry();
                    return;
                }
                // Manual text is replaced only through the same explicit confirmation as the result toolbar.
                if (!rebuildText && model.KeepCurrentManualResult()) return;
                await GenerateResultAsync(model, openResult: rebuildText); return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            model.Error = "Не удалось завершить расчёт. Сохранённые расчёты и введённые правки остались; повторите «Рассчитать».";
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
        finally { _calculatingReports = false; }
    }

    private async void OnResolveCalculation(object? sender, RoutedEventArgs e)
    {
        if (Model is not { CanUse: true } model || OwnedWindows.Count > 0) return;
        switch (model.GoToCalculationProblem())
        {
            case CalculationTarget.Header:
                OrderHeaderExpander.IsExpanded = true;
                Dispatcher.UIThread.Post(() => { if (IsVisible) SaveWorkflowHeader.Focus(); }, DispatcherPriority.Loaded);
                break;
            case CalculationTarget.Project:
                OnGoToProject(sender, e);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!IsVisible) return;
                    if (model.IsPlanning) CreateWorkflowProject.Focus();
                    else if (model.SelectedDraft?.IsDirty == true) SaveWorkflowSpecification.Focus();
                    else if (model.SelectedProject is null) PrepareWorkflowProject.Focus();
                }, DispatcherPriority.Loaded);
                break;
            case CalculationTarget.Reports:
            case CalculationTarget.Recalculate: OnCalculateOrder(sender, e); break;
            case CalculationTarget.Refresh: model.ReloadCommand.Execute(null); break;
            case CalculationTarget.Settings: await ShowSettingsAsync(0); break;
            case CalculationTarget.Folder:
                if (model.SelectedProject?.Project.NativePath is { } path)
                    try { if (!await OpenFolderAsync(Path.GetDirectoryName(path)!)) model.Error = "Папка проекта недоступна. Проверьте путь и доступ к сети."; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { model.Error = "Не удалось открыть папку проекта. Проверьте доступ к сети."; }
                break;
        }
    }
}
