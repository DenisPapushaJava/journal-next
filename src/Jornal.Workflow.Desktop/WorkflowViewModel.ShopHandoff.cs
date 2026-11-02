using Avalonia.Media;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    public bool HasRecordedShopHandoff => CurrentOrder is { } order && (order.HasShopHandoff || order.Projects.Any(p => p.ShopHandoff is not null));
    public bool CanClearShopHandoff => CardWriteProblem is null && HasRecordedShopHandoff;
    public string ClearShopHandoffHint => CardWriteProblem ?? (HasRecordedShopHandoff ? "Убрать отметку; файлы в цехе останутся" : "У заявки нет отметки передачи");
    internal sealed record HandoffClear(IWorkflowWorkspaceStore Source, Guid OrderId, long Version, WorkflowHandoffClearExpected Expected, Guid Actor);
    internal HandoffClear? PrepareHandoffClear() => CanClearShopHandoff && CurrentOrder is { } order
        ? new(_store, order.Id, order.Version, WorkflowHandoffClearExpected.From(order), ActorId()) : null;
    internal async Task<bool> ClearShopHandoffAsync(HandoffClear request, CancellationToken token = default)
    {
        if (!CanClearShopHandoff || !ReferenceEquals(request.Source, _store) || CurrentOrder?.Id != request.OrderId ||
            CurrentOrder.Version != request.Version || !request.Expected.Matches(CurrentOrder)) return false;
        var cleared = false;
        await RunAsync(async () =>
        {
            var next = await request.Source.ClearShopHandoffAsync(request.OrderId, request.Version, request.Expected, request.Actor, token);
            if (_cuttingTaskDetached || !ReferenceEquals(request.Source, _store) || CurrentOrder?.Id != request.OrderId) return;
            _workspace = next; ResetCuttingTask(); RefreshOrders(preserveRows: true); NotifyOrder(); cleared = true;
        });
        return cleared;
    }

    public uint ShopHandoffColorArgb => _localPreferences.ShopHandoffColorArgb;
    public IBrush ShopHandoffBrush => new SolidColorBrush(Color.FromUInt32(ShopHandoffColorArgb));
    private void NotifyShopHandoffColor()
    {
        OnPropertyChanged(nameof(ShopHandoffColorArgb)); OnPropertyChanged(nameof(ShopHandoffBrush));
        foreach (var row in Orders) row.ApplyShopHandoffColor(ShopHandoffColorArgb);
        NotifyProjectHandoffs();
    }
    internal string? SaveShopHandoffColor(uint expected, uint color)
    {
        if (Updates.IsApplying) return "Дождитесь завершения обновления.";
        try
        {
            var saved = _preferences.Update(current =>
            {
                if (current.ShopHandoffColorArgb != expected && current.ShopHandoffColorArgb != color)
                    throw new InvalidOperationException("Цвет изменён в другом окне. Закройте выбор цвета и откройте его заново.");
                return current.ShopHandoffColorArgb == color ? current : current with { ShopHandoffColorArgb = color };
            });
            _localPreferences = _localPreferences with { ShopHandoffColorArgb = saved.ShopHandoffColorArgb };
            NotifyShopHandoffColor(); return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or InvalidOperationException)
        { return ex is InvalidOperationException ? ex.Message : "Не удалось сохранить цвет на этом компьютере. Проверьте доступ к локальным настройкам."; }
    }
}
