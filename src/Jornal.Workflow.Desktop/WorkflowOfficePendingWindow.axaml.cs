using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Workflow.Core;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowOfficePendingWindow : Window
{
    private readonly WorkflowViewModel? _model;
    private readonly bool _closeApplication;
    private readonly CancellationToken _ownerToken;
    private WorkflowOfficePendingFiles _files = new([], []);
    private CancellationTokenSource? _sending;

    public WorkflowOfficePendingWindow() => InitializeComponent();

    internal WorkflowOfficePendingWindow(WorkflowViewModel model, WorkflowOfficePendingFiles files, bool closeApplication,
        CancellationToken ownerToken) : this()
    {
        _model = model;
        _files = files;
        _closeApplication = closeApplication;
        _ownerToken = ownerToken;
        Title = "Файлы для офиса";
        Render();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        KeepWorking.Focus();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_sending is not null && e.CloseReason != WindowCloseReason.OSShutdown)
        {
            e.Cancel = true;
            CancelSending();
        }
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            Continue();
            e.Handled = true;
        }
    }

    private void OnContinue(object? sender, RoutedEventArgs e) => Continue();
    private void OnExit(object? sender, RoutedEventArgs e) => Close(true);

    private void Continue()
    {
        if (_sending is not null) CancelSending();
        else Close(false);
    }

    private void CancelSending()
    {
        _sending?.Cancel();
        KeepWorking.IsEnabled = false;
        PendingSummary.Text = "Останавливаем отправку…";
    }

    private async void OnSend(object? sender, RoutedEventArgs e)
    {
        if (_model is not { } model || _sending is not null) return;
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(_ownerToken);
        _sending = sending;
        var requested = _files;
        var total = requested.Projects.Length;
        WorkflowOfficeFileBatch? result = null;
        var problems = new List<string>();
        var reportProgress = true;
        Render();
        KeepWorking.Focus();
        try
        {
            if (total > 0)
            {
                var progress = new Progress<int>(completed =>
                {
                    if (!reportProgress || !IsVisible || !ReferenceEquals(_sending, sending) || sending.IsCancellationRequested) return;
                    PendingProgress.IsIndeterminate = false;
                    PendingProgress.Value = completed;
                    PendingSummary.Text = $"Обработано проектов: {completed} из {total}.";
                });
                result = await model.PublishOfficePendingFilesAsync(requested, progress, sending.Token);
                problems.AddRange(result.Problems);
            }
            reportProgress = false;
            PendingSummary.Text = "Проверяем оставшиеся файлы…";
            var remaining = await model.RefreshOfficePendingFilesAsync(sending.Token).WaitAsync(sending.Token);
            if (remaining is not null) _files = remaining;
            else if (!sending.IsCancellationRequested) problems.Add("Не удалось повторно проверить файлы.");
        }
        catch (OperationCanceledException) when (sending.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problems.Add(ex is WorkflowValidationException ? ex.Message : "Не удалось завершить отправку. Проверьте связь с офисом.");
            AppDiagnostics.Capture(DiagnosticOperation.Unexpected, ex);
        }
        finally { _sending = null; }
        if (!IsVisible || _ownerToken.IsCancellationRequested) return;
        if (sending.IsCancellationRequested)
        {
            _ = model.RefreshOfficePendingFilesAsync(_ownerToken);
            Close(false);
            return;
        }
        if (_closeApplication && problems.Count == 0 && _files is { Projects.Length: 0, Incomplete: false })
        {
            Close(true);
            return;
        }
        var status = result is null ? null : $"Отправлено файлов: {result.Sent}. Проектов без ошибок: {result.Completed} из {total}.";
        Render(status, problems);
        KeepWorking.Focus();
    }

    private void Render(string? status = null, List<string>? problems = null)
    {
        var running = _sending is not null;
        var count = _files.Projects.Length;
        PendingProgress.IsVisible = running;
        if (running)
        {
            PendingProgress.IsIndeterminate = true;
            PendingProgress.Maximum = Math.Max(count, 1);
            PendingProgress.Value = 0;
        }
        PendingProblems.Text = problems is null ? "" : string.Join("\n", problems);
        PendingDetails.IsVisible = !running && problems is { Count: > 0 };
        var summary = count > 0 ? $"Проектов с неотправленными файлами: {count}. Файлы хранятся на этом компьютере." : "";
        if (_files.Incomplete)
            summary = Join(summary, "Некоторые локальные копии недоступны или заняты. Проверьте папки и закройте открытые файлы.");
        else if (count == 0 && status is null)
            summary = "Неотправленных файлов нет.";
        if (count > 0 && _model is { CanPublishOfficeFiles: false })
            summary = Join(summary, "На сервере выключено удалённое сохранение.");
        PendingSummary.Text = running ? count > 0 ? "Готовим отправку в офис…" : "Проверяем локальные файлы…" : Join(status, summary);
        KeepWorking.Label = running ? "Остановить" : _closeApplication ? "Продолжить работу" : "Закрыть";
        KeepWorking.IsEnabled = true;
        ExitWithoutSending.IsVisible = _closeApplication && !running;
        SendPendingFiles.IsVisible = !running && (count > 0 && _model is { CanPublishOfficeFiles: true } || count == 0 && _files.Incomplete);
        SendPendingFiles.Label = count == 0 ? "Проверить снова" : _closeApplication ? "Отправить и закрыть" : "Отправить в офис";
    }

    private static string Join(string? first, string second) => string.IsNullOrEmpty(first) ? second :
        string.IsNullOrEmpty(second) ? first : first + "\n" + second;
}
