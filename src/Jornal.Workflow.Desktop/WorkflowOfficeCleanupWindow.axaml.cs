using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowOfficeCleanupWindow : Window
{
    private readonly WorkflowViewModel? _model;
    private readonly WorkflowOfficeStorageClient? _client;
    private CancellationTokenSource? _operation;
    private WorkflowOfficeCleanupPlan? _plan;

    public WorkflowOfficeCleanupWindow() => InitializeComponent();

    internal WorkflowOfficeCleanupWindow(WorkflowViewModel model, WorkflowOfficeStorageClient client) : this() =>
        (_model, _client) = (model, client);

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        CancelCleanup.Focus();
        if (_model is not { } model || _client is not { } client) return;
        await RunAsync("Проверяем локальные копии и сверяем их с офисом…", async token =>
        {
            _plan = await model.PreviewOfficeCleanupAsync(client, token);
            var count = _plan.Copies.Length;
            CleanupSummary.Text = count > 0
                ? $"Можно освободить около {FormatBytes(_plan.Copies.Sum(copy => copy.Bytes))}."
                : _plan.Skipped.Length == 0 ? "На этом компьютере нет скачанных проектов." :
                    "Нет локальных копий, которые можно безопасно удалить.";
            CleanupCandidates.Header = $"Копии для удаления · {count}";
            CleanupCopyNames.Text = string.Join("\n", _plan.Copies.Select(copy => copy.Project.Name).Order(StringComparer.CurrentCulture));
            ShowSkipped(_plan.Skipped);
        });
    }

    private async void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_operation is not null || _model is not { } model || _client is not { } client ||
            _plan is not { Copies.Length: > 0 } plan) return;
        _plan = null;
        await RunAsync("Перепроверяем и удаляем локальные копии…", async token =>
        {
            var result = await model.RemoveOfficeCopiesAsync(client, plan, token);
            var summary = $"Удалено локальных копий: {result.Removed}. Освобождено {FormatBytes(result.Bytes)}.";
            CleanupSummary.Text = result.StopReason is { } reason ? reason + "\n" + summary :
                result.Stopped ? "Очистка остановлена. " + summary : summary;
            ShowSkipped([.. plan.Skipped, .. result.Skipped]);
        });
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_operation is not null)
        {
            _operation.Cancel();
            if (e.CloseReason != WindowCloseReason.OSShutdown)
            {
                e.Cancel = true;
                CancelCleanup.IsEnabled = false;
                CleanupSummary.Text = "Останавливаем…";
            }
        }
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async Task RunAsync(string message, Func<CancellationToken, Task> run)
    {
        using var operation = new CancellationTokenSource();
        _operation = operation;
        CleanupProgress.IsVisible = true;
        CleanupSummary.Text = message;
        CleanupHint.IsVisible = CleanupCandidates.IsVisible = CleanupDetails.IsVisible = ConfirmCleanup.IsVisible = false;
        CancelCleanup.Label = "Остановить";
        try { await run(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        { CleanupSummary.Text = "Проверка остановлена."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CleanupSummary.Text = ex is WorkflowValidationException or WorkflowOfficeSyncUnavailableException ? ex.Message :
                "Не удалось проверить локальные копии. Проверьте доступ к папкам и связь с офисом.";
            if (ex is not WorkflowOfficeSyncUnavailableException) AppDiagnostics.Capture(DiagnosticOperation.Unexpected, ex);
        }
        finally
        {
            _operation = null;
            if (IsVisible)
            {
                var canConfirm = _plan is { Copies.Length: > 0 };
                CleanupProgress.IsVisible = false;
                CleanupHint.IsVisible = CleanupCandidates.IsVisible = ConfirmCleanup.IsVisible = canConfirm;
                CancelCleanup.Label = canConfirm ? "Отмена" : "Закрыть";
                CancelCleanup.IsEnabled = true;
                CancelCleanup.Focus();
            }
        }
    }

    private void ShowSkipped(string[] skipped)
    {
        CleanupDetails.IsVisible = skipped.Length > 0;
        CleanupSkippedCount.Text = $"Пропущено копий: {skipped.Length}";
        CleanupSkipped.Text = string.Join("\n", skipped);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):0.#} ГиБ",
        >= 1024 * 1024 => $"{bytes / (1024d * 1024):0.#} МиБ",
        >= 1024 => $"{bytes / 1024d:0.#} КиБ",
        _ => $"{bytes} Б"
    };
}
