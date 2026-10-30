using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    public bool IsCurrentOrderMissing => _openOrderId is { } id && _loadedOrderSnapshot?.Id == id && !_workspace.Orders.Any(o => o.Id == id);
    public bool IsCurrentOrderActive => !IsCurrentOrderMissing && CurrentOrder is { IsDeleted: false };
    public bool IsCurrentCardStale => CurrentOrder is { } order && CardVersion != order.Version;
    public bool HasCardReview => IsCurrentOrderMissing || CurrentOrder?.IsDeleted == true || IsCurrentCardStale;
    public string CardReviewText => IsCurrentOrderMissing ?
        "Заявка окончательно удалена в другом окне. Локальный черновик сохранён; запись в удалённую заявку недоступна. Скопируйте нужные данные или явно отмените правки." : CurrentOrder?.IsDeleted == true ?
        "Заявка находится в корзине. Черновик остаётся в этой карточке; для продолжения восстановите заявку." :
        "Версия заявки изменилась. Ваш ввод сохранён. Сверьте данные; можно отменить правки и обновить карточку.";
    public string TrashCaption => "Корзина (" + (_store is Infrastructure.IWorkflowQueryStore query ? query.TrashCount : _workspace.Orders.Count(o => o.IsDeleted)) + ")";

    public WorkflowTrashConfirmationViewModel? CreateTrashConfirmation()
    {
        if (!CanUse || !IsCurrentOrderActive || CurrentOrder is not { IsDeleted: false } order || IsCreating) return null;
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
    public WorkflowTrashViewModel CreateTrash()
    {
        CancelDatabaseSearch(); ConfigureDatabaseQuery();
        return new(() => _workspace, ReadTrashAsync, RestoreFromTrashAsync, PurgeFromTrashAsync,
        _store is Infrastructure.IWorkflowQueryStore ? ReadMoreTrashAsync : null,
        () => _store is Infrastructure.IWorkflowQueryStore { HasMoreTrash: true },
        _store is Infrastructure.IWorkflowQueryStore ? () => ((Infrastructure.IWorkflowQueryStore)_store).TrashCount : null);
    }
    private async Task<WorkflowWorkspace> ReadMoreTrashAsync(CancellationToken token)
    {
        if (!CanUse || _store is not Infrastructure.IWorkflowQueryStore store) throw new WorkflowValidationException("Дождитесь завершения операции.");
        var book = await store.ReadMoreTrashAsync(token); ApplyTrashSnapshot(book); return book;
    }
    private async Task<WorkflowWorkspace> ReadTrashAsync(CancellationToken token)
    {
        if (!CanUse) throw new WorkflowValidationException("Дождитесь завершения операции.");
        var book = await _store.ReadTrashAsync(token); token.ThrowIfCancellationRequested();
        ApplyTrashSnapshot(book); return book;
    }
    private void ApplyTrashSnapshot(WorkflowWorkspace book)
    {
        CaptureCard(); var dirty = HasCurrentCardEdits; _loadedOrderSnapshot = CurrentOrder; _workspace = book;
        if (!IsCreating && !dirty && IsCurrentOrderMissing && string.IsNullOrEmpty(ResultText)) ForgetOpenCard();
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
    private async Task<string?> PurgeFromTrashAsync(IReadOnlyList<WorkflowTrashTarget> targets, CancellationToken token)
    {
        if (!CanUse) return "Дождитесь завершения операции.";
        if (targets.Any(t => HasDraftForOrder(t.OrderId)))
            return "У удаляемой заявки есть несохранённый черновик. Сначала восстановите заявку и сохраните или отмените правки.";
        var saved = false;
        await RunAsync(async () =>
        {
            var book = await _store.PurgeTrashedOrdersAsync(targets, ActorId(), token);
            var ids = targets.Select(t => t.OrderId).ToHashSet();
            foreach (var id in ids) _cards.Remove(id);
            if (_openOrderId is { } openId && ids.Contains(openId))
                ForgetOpenCard();
            ApplyTrashSnapshot(book); saved = true; OnPropertyChanged(nameof(HasPendingEdits));
        }, fileError: "Не удалось подтвердить удаление: файл данных недоступен или занят.");
        if (!saved && Error is null) token.ThrowIfCancellationRequested();
        return saved ? null : Error ?? "Не удалось подтвердить удаление. Обновите корзину перед повторной операцией.";
    }
    private void ForgetOpenCard()
    {
        ResetResultPresentation();
        _openOrderId = null; _loadedOrderVersion = null; _loadedOrderSnapshot = null;
        Drafts.Clear(); SelectedDraft = null; Projects.Clear(); SelectedProject = null;
        ResultText = ""; IsResultExpanded = false; ResultIsStale = false;
        IsPlanning = false; _preparedProject = null; _redoItem = null;
    }
    [RelayCommand] private void DiscardCardChanges()
    {
        if (!CanUse || IsCreating || CurrentOrder is not { } order) return;
        if (IsCurrentOrderMissing)
        {
            var preparedPath = _preparedProject?.NativePath;
            _cards.Remove(order.Id); ForgetOpenCard(); RefreshOrders();
            if (SelectedOrder is { } next) SwitchCard(next.Order);
            NotifyOrder();
            if (preparedPath is not null) Error = "Созданная заготовка осталась на диске: " + preparedPath;
            return;
        }
        if (_preparedProject is not null)
        { Error = "Заготовка проекта уже создана. Сначала восстановите заявку и сохраните подготовленный проект."; return; }
        _cards.Remove(order.Id); IsPlanning = false; _redoItem = null;
        LoadOrder(order); // Same-order reload preserves manually edited letter text.
    }
}
