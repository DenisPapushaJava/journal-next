using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    public bool IsCurrentOrderActive => CurrentOrder is { IsDeleted: false };
    public bool IsCurrentCardStale => CurrentOrder is { } order && CardVersion != order.Version;
    public bool HasCardReview => CurrentOrder?.IsDeleted == true || IsCurrentCardStale;
    public string CardReviewText => CurrentOrder?.IsDeleted == true ?
        "Заявка находится в корзине. Черновик остаётся в этой карточке; для продолжения восстановите заявку." :
        "Версия заявки изменилась. Ваш ввод сохранён. Сверьте данные; можно отменить правки и обновить карточку.";
    public string TrashCaption => "Корзина (" + _workspace.Orders.Count(o => o.IsDeleted) + ")";

    public WorkflowTrashConfirmationViewModel? CreateTrashConfirmation()
    {
        if (!CanUse || CurrentOrder is not { IsDeleted: false } order || IsCreating) return null;
        if (CurrentEmployee is null) { Error = "Выберите себя: Настройки → Общие → Технолог."; return null; }
        if (HasCurrentCardEdits)
        { Error = "Сохраните или отмените правки текущей карточки перед перемещением в корзину."; return null; }
        if (IsCurrentCardStale)
        { Error = "Обновите карточку и проверьте актуальную заявку перед перемещением в корзину."; return null; }
        return new(order, CustomerName(order.CustomerId), token => MoveToTrashAsync(order.Id, order.Version, token));
    }
    private async Task<string?> MoveToTrashAsync(Guid id, long version, CancellationToken token)
    {
        if (!CanUse) return "Дождитесь завершения операции.";
        if (CurrentOrder?.Id != id || HasCurrentCardEdits || CardVersion != version || CurrentOrder.Version != version)
            return "Карточка изменилась. Закройте подтверждение и проверьте заявку.";
        var saved = false;
        await RunAsync(async () =>
        {
            var actor = ActorId(); CaptureCard();
            _workspace = await _store.WriteOrderAsync(id, version, book => WorkflowChanges.MoveOrderToTrash(book, id, version,
                actor, DateTimeOffset.UtcNow), token);
            saved = true; Back(); RefreshOrders();
            if (SelectedOrder is { } next) SwitchCard(next.Order);
            NotifyOrder();
        });
        if (!saved && Error is null) token.ThrowIfCancellationRequested();
        return saved ? null : Error ?? "Не удалось переместить заявку в корзину.";
    }
    public WorkflowTrashViewModel CreateTrash() => new(() => _workspace, ReadTrashAsync, RestoreFromTrashAsync);
    private async Task<WorkflowWorkspace> ReadTrashAsync(CancellationToken token)
    {
        if (!CanUse) throw new WorkflowValidationException("Дождитесь завершения операции.");
        var book = await _store.ReadAsync(token); token.ThrowIfCancellationRequested();
        ApplyTrashSnapshot(book); return book;
    }
    private void ApplyTrashSnapshot(WorkflowWorkspace book)
    {
        var dirty = HasCurrentCardEdits; _workspace = book;
        RefreshEmployees(CurrentEmployee?.Id); RefreshOrders();
        if (!IsCreating && !dirty && CurrentOrder is { } order) LoadOrder(order);
        else if (!IsCreating && !dirty && CurrentOrder is null && SelectedOrder is { } selected) SwitchCard(selected.Order);
        NotifyOrder();
    }
    private async Task<string?> RestoreFromTrashAsync(Guid id, long version, CancellationToken token)
    {
        if (!CanUse) return "Дождитесь завершения операции.";
        var saved = false;
        await RunAsync(async () =>
        {
            var actor = ActorId();
            var book = await _store.WriteOrderAsync(id, version, current => WorkflowChanges.RestoreOrder(current, id, version,
                actor, DateTimeOffset.UtcNow), token, allowTrashed: true);
            ApplyTrashSnapshot(book); saved = true;
        });
        if (!saved && Error is null) token.ThrowIfCancellationRequested();
        return saved ? null : Error ?? "Не удалось восстановить заявку.";
    }
    [RelayCommand] private void DiscardCardChanges()
    {
        if (!CanUse || IsCreating || CurrentOrder is not { } order) return;
        if (_preparedProject is not null)
        { Error = "Заготовка проекта уже создана. Сначала восстановите заявку и сохраните подготовленный проект."; return; }
        _cards.Remove(order.Id); IsPlanning = false; _redoItem = null;
        LoadOrder(order); // Same-order reload preserves manually edited letter text.
    }
}
