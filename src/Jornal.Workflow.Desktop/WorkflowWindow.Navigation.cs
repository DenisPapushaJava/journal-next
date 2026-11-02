using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private bool _closeDialogPending;
    private bool _confirmedApplicationClose;

    private async void OnCloseCard(object? sender, RoutedEventArgs e) => await RequestCloseAsync(application: false);

    private async Task RequestCloseAsync(bool application)
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
}
