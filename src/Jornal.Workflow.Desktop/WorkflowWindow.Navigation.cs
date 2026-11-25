using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private bool _closeDialogPending;
    private bool _confirmedApplicationClose;
    private TaskCompletionSource<bool>? _officeCheckChoice;
    private bool _officeCheckForExit;
    private bool _closeAfterOfficeCheck;

    private async void OnCloseCard(object? sender, RoutedEventArgs e) => await RequestCloseAsync(application: false);

    private async Task RequestCloseAsync(bool application, bool checkOfficeFiles = false)
    {
        if (_closeDialogPending || OwnedWindows.Count > 0 || Model is not { } model || model.PrepareClose(application) is not { } request) return;
        _closeDialogPending = true;
        try
        {
            var choice = request.NeedsConfirmation ? await new WorkflowCloseWindow(request)
                { RequestedThemeVariant = ActualThemeVariant }.ShowDialog<WorkflowCloseChoice?>(this) ?? WorkflowCloseChoice.Cancel : WorkflowCloseChoice.Discard;
            if (!IsVisible || !ReferenceEquals(Model, model)) return;
            if (choice == WorkflowCloseChoice.Cancel) return;
            if (!model.IsCloseRequestCurrent(request))
            { model.Error = "Данные или ввод изменились. Проверьте карточку перед закрытием."; return; }
            if (choice == WorkflowCloseChoice.Save)
            {
                if (!await model.SaveCloseDataAsync(request)) return;
                request = model.PrepareClose(application);
                if (request is null || request.NeedsConfirmation || !ReferenceEquals(Model, model) || !IsVisible) return;
            }
            if (application)
            {
                if (checkOfficeFiles)
                {
                    // Let the initial Closing event return before a clean check can request closure again.
                    await Task.Yield();
                    if (!IsVisible || !ReferenceEquals(Model, model) || !model.IsCloseRequestCurrent(request) ||
                        !await ShowOfficePendingFilesAsync(model, request)) return;
                    if (!IsVisible || !ReferenceEquals(Model, model) || !model.IsCloseRequestCurrent(request)) return;
                }
                _confirmedApplicationClose = true;
                try { Close(); } finally { _confirmedApplicationClose = false; }
            }
            else if (model.CloseCard(request)) WorkflowSearchInput.Focus();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsVisible && ReferenceEquals(Model, model)) model.Error = "Не удалось завершить закрытие. Ваш ввод остаётся в программе.";
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
        finally { _closeDialogPending = false; }
    }

    private async void OnOfficePendingFiles(object? sender, RoutedEventArgs e)
    {
        if (_closeDialogPending || OwnedWindows.Count > 0 || Model is not { HasOfficeWorkingCopies: true, CanUse: true } model) return;
        _closeDialogPending = true;
        try { await ShowOfficePendingFilesAsync(model); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsVisible && ReferenceEquals(Model, model)) model.Error = "Не удалось проверить локальные файлы.";
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
        finally
        {
            _closeDialogPending = false;
            if (_closeAfterOfficeCheck)
            {
                _closeAfterOfficeCheck = false;
                if (IsVisible && !_lifetime.IsCancellationRequested) Close();
            }
        }
    }

    private async Task<bool> ShowOfficePendingFilesAsync(WorkflowViewModel model, WorkflowCloseRequest? exit = null)
    {
        var token = _lifetime.Token;
        var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _officeCheckChoice = choice;
        _officeCheckForExit = exit is not null;
        using var checking = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var registration = token.Register(() => choice.TrySetCanceled(token));
        SkipOfficeFilesCheck.Content = _officeCheckForExit ? "Закрыть без отправки" : "Отменить проверку";
        SkipOfficeFilesCheck.SetValue(ToolTip.TipProperty, _officeCheckForExit ? "Файлы останутся на этом компьютере" : "Esc");
        WorkflowOfficeFilesChecking.IsVisible = true;
        WorkflowOfficePendingFiles? files;
        try
        {
            var scan = model.RefreshOfficePendingFilesAsync(checking.Token);
            await Task.WhenAny(scan, choice.Task);
            if (choice.Task.IsCompleted)
            {
                checking.Cancel();
                return choice.Task.IsCompletedSuccessfully && await choice.Task &&
                    IsVisible && ReferenceEquals(Model, model) && OwnedWindows.Count == 0;
            }
            files = await scan;
        }
        finally
        {
            _officeCheckChoice = null;
            _officeCheckForExit = false;
            WorkflowOfficeFilesChecking.IsVisible = false;
        }
        if (files is null || !IsVisible || !ReferenceEquals(Model, model) || token.IsCancellationRequested || OwnedWindows.Count > 0)
            return false;
        if (exit is not null)
        {
            if (!model.IsCloseRequestCurrent(exit))
            {
                model.Error = "Данные или ввод изменились. Проверьте карточку перед закрытием.";
                return false;
            }
            if (files is { Projects.Length: 0, Incomplete: false }) return true;
        }
        return await new WorkflowOfficePendingWindow(model, files, closeApplication: exit is not null, token)
            { RequestedThemeVariant = ActualThemeVariant }.ShowDialog<bool>(this);
    }

    private void OnSkipOfficeFilesCheck(object? sender, RoutedEventArgs e) => _officeCheckChoice?.TrySetResult(_officeCheckForExit);
}
