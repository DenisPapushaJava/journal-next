using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private async void OnClearShopHandoff(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Model is not { } model || OwnedWindows.Count > 0 || model.PrepareHandoffClear() is not { } request) return;
        try
        {
            if (!await new WorkflowHandoffClearWindow { RequestedThemeVariant = ActualThemeVariant }.ShowDialog<bool>(this)) return;
            if (IsVisible && ReferenceEquals(Model, model) && !_lifetime.IsCancellationRequested) await model.ClearShopHandoffAsync(request, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsVisible && ReferenceEquals(Model, model)) model.Error = "Не удалось снять отметку. Обновите журнал и проверьте её состояние."; }
    }
}
