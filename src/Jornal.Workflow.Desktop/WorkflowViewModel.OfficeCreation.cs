using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private sealed class UnconfirmedOfficeOrderException : Exception { }
    private Guid? _officeProjectToRecover;
    private WorkflowOfficeOrderCreation? _openedSavedOrderDraft;
    private bool HasUnsavedNewOrderFields => _openedSavedOrderDraft is { } draft
        ? NewManager.Trim() != draft.Manager || NewCustomer.Trim() != draft.Customer
        : !string.IsNullOrWhiteSpace(NewManager) || !string.IsNullOrWhiteSpace(NewCustomer);
    private Guid? OfflineOrderActor => _connection.EmployeeId is { } actor && OfflineJournal?.HasTechnologist(actor) == true ? actor : null;

    private void TryRestoreOfficeCreation(Action restore)
    {
        try { restore(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
        { Error = ex is WorkflowValidationException ? ex.Message : "Не удалось открыть локальные данные незавершённого создания."; }
    }

    private void RestoreOfficeOrderForm()
    {
        if (_connection.RemoteClient is { } remote && (OfflineOrderActor ?? ActiveEmployee?.Id) is { } actor &&
            remote.ReadOrderCreation(actor) is { } pending)
            ShowOfficeOrderForm(pending);
    }

    private void ShowOfficeOrderForm(WorkflowOfficeOrderCreation pending)
    {
        IsCreating = true;
        LoadSavedOrderFields(pending);
        Error = pending.NotSent ? "Заявка ещё не отправлена. Нажмите «Создать», чтобы продолжить." :
            "Создание заявки не подтверждено. Нажмите «Создать», чтобы продолжить.";
    }

    private void LoadSavedOrderFields(WorkflowOfficeOrderCreation pending)
    {
        _openedSavedOrderDraft = pending is { SavedOffline: true, NotSent: true } ? pending : null;
        NewManager = pending.Manager;
        NewCustomer = pending.Customer;
    }

    private async Task<bool> RecoverOfficeOrderAsync(CancellationToken token)
    {
        if (_connection.RemoteClient is not { } remote || ActiveEmployee is not { } actor ||
            remote.ReadOrderCreation(actor.Id) is null)
            return false;
        using var lease = remote.LockCreation(actor.Id);
        if (remote.ReadOrderCreation(actor.Id) is not { } current)
            return false;
        if (!_workspace.Orders.Any(order => order.Id == current.Id))
            _workspace = await _store.ReadOrderAsync(current.Id, token);
        token.ThrowIfCancellationRequested();
        if (_workspace.Orders.SingleOrDefault(order => order.Id == current.Id) is not { } created)
        {
            if (!IsCreating) ShowOfficeOrderForm(current);
            return true;
        }
        remote.CompleteCreation(actor.Id);
        ShowCreatedOrder(created, actor.Id);
        return true;
    }

    private void RestoreOfficeProjectForm(WorkflowOrder order)
    {
        if (_connection.RemoteClient is not { } remote || ActiveEmployee is not { } actor ||
            remote.ReadProjectCreation(actor.Id, order.Id) is not { } pending)
            return;
        ShowOfficeProjectForm(pending);
        Error = "Создание проекта не подтверждено. Нажмите «Создать проект», чтобы продолжить.";
    }

    private void ShowOfficeProjectForm(WorkflowOfficeProjectPreparation pending)
    {
        _officeProjectToRecover = pending.ProjectId;
        _redoItem = pending.ItemId;
        PlanMachine = _workspace.Machines.SingleOrDefault(machine => machine.Id == pending.MachineId);
        PlanThickness = SpecificationDraft.N(pending.ThicknessMm);
        PlanHasBending = pending.HasPlannedBending;
        IsPlanning = true;
        OnPropertyChanged(nameof(PlanTitle));
    }

    private async Task CreateOfficeProjectAsync(WorkflowOfficeStorageClient remote, WorkflowOrder order, Guid actor)
    {
        using var lease = remote.LockCreation(actor, order.Id);
        var pending = remote.ReadProjectCreation(actor, order.Id);
        var target = order;
        if (RequiresDatabaseRefresh)
        {
            ConfigureDatabaseQuery();
            _workspace = await _store.ReadAsync();
            target = RequireOfficeProjectOrder(order.Id);
        }
        else if (pending is not null || PlanHasBending && OfficeSchemaNeedsBendingUpgrade)
            target = await ReadOfficeProjectOrderAsync(order.Id);
        if (pending is not null && target.Projects.SingleOrDefault(project => project.Id == pending.ProjectId) is { } saved)
        {
            CompleteOfficeProject(remote, target, actor, saved);
            return;
        }

        if (target.IsDeleted || target.HasProjectRename)
            throw new WorkflowValidationException("Сначала восстановите заявку или завершите изменение параметров проекта.");
        var machine = PlanMachine ?? throw new WorkflowValidationException("Выберите станок.");
        var thickness = SpecificationDraft.Decimal(PlanThickness, "толщину");
        if (pending is not null && (pending.MachineId != machine.Id || pending.ThicknessMm != thickness ||
            pending.ItemId != _redoItem || pending.HasPlannedBending != PlanHasBending))
        {
            ShowOfficeProjectForm(pending);
            Error = "Восстановлены параметры незавершённого проекта. Нажмите «Создать проект», чтобы продолжить.";
            return;
        }
        if (_redoItem is { } item && !target.ActiveProjects.Any(project => project.ItemId == item))
            throw new WorkflowValidationException("Исходная позиция находится в корзине. Восстановите её перед созданием варианта.");

        var request = pending ?? new(Guid.NewGuid(), target.Version, actor, machine.Id, thickness, _redoItem, PlanHasBending);
        try
        {
            var prepared = await PrepareOfficeProjectAsync(remote, order.Id, request with { OrderVersion = target.Version }, pending is not null);
            target = await ReadOfficeProjectOrderAsync(order.Id);
            var project = target.Projects.SingleOrDefault(project => project.Id == request.ProjectId);
            if (project is null)
            {
                if (target.IsDeleted || target.HasProjectRename ||
                    request.ItemId is { } previousItem && !target.ActiveProjects.Any(p => p.ItemId == previousItem))
                    throw new WorkflowValidationException("Позиция или параметры заявки изменились в офисе. Проверьте их перед сохранением проекта.");
                prepared = prepared with { Variant = target.Projects.Where(p => p.ItemId == prepared.ItemId).Select(p => p.Variant).DefaultIfEmpty().Max() + 1 };
                _workspace = await _store.WriteOrderAsync(order.Id, target.Version, book =>
                    WorkflowProjects.Add(book, order.Id, target.Version, prepared, actor, DateTimeOffset.UtcNow));
                target = _workspace.Orders.Single(o => o.Id == order.Id);
                project = target.Projects.SingleOrDefault(p => p.Id == request.ProjectId) ?? throw new WorkflowOutcomeUnknownException();
            }
            CompleteOfficeProject(remote, target, actor, project);
        }
        catch (WorkflowOutcomeUnknownException)
        {
            throw new WorkflowValidationException("Сервер не подтвердил сохранение проекта. Нажмите «Создать проект» ещё раз, чтобы проверить результат.");
        }
    }

    private async Task<CuttingProject> PrepareOfficeProjectAsync(WorkflowOfficeStorageClient remote, Guid order,
        WorkflowOfficeProjectPreparation request, bool retry)
    {
        if (request.HasPlannedBending && OfficeSchemaNeedsBendingUpgrade)
            throw new WorkflowValidationException("Для отметки гибки до расчёта откройте MDB в обновлённых «Заявках» на рабочем ПК.");

        try
        {
            return await remote.PrepareProjectAsync(order, request, CancellationToken.None, () =>
            {
                if (!retry) remote.SaveCreation(request.ActorId, order, request);
                _officeProjectToRecover = request.ProjectId;
            });
        }
        catch (WorkflowOfficeProjectNotStartedException ex)
        {
            if (!retry) ClearRejectedOfficeProject(remote, order, request.ActorId);
            throw new WorkflowValidationException(ex.Message);
        }
        catch (WorkflowConflictException) when (!retry)
        {
            ClearRejectedOfficeProject(remote, order, request.ActorId);
            var latest = await ReadOfficeProjectOrderAsync(order);
            ShowRecoveredOfficeProjectOrder(latest);
            throw new WorkflowValidationException("Заявка изменилась в офисе. Проверьте параметры и повторите создание проекта.");
        }
    }

    private bool OfficeSchemaNeedsBendingUpgrade =>
        _store is WorkflowMdbStore { OfficeSchemaVersion: < WorkflowMdbSchema.PlannedBendingVersion };

    private void ClearRejectedOfficeProject(WorkflowOfficeStorageClient remote, Guid order, Guid actor)
    {
        remote.CompleteCreation(actor, order);
        _officeProjectToRecover = null;
    }

    private async Task<WorkflowOrder> ReadOfficeProjectOrderAsync(Guid order)
    {
        _workspace = await _store.ReadOrderAsync(order);
        return RequireOfficeProjectOrder(order);
    }

    private WorkflowOrder RequireOfficeProjectOrder(Guid order) => _workspace.Orders.SingleOrDefault(o => o.Id == order) ??
        throw new WorkflowValidationException("Заявки больше нет в офисной базе. Обновите журнал.");

    private void CompleteOfficeProject(WorkflowOfficeStorageClient remote, WorkflowOrder order, Guid actor, CuttingProject project)
    {
        remote.CompleteCreation(actor, order.Id);
        IsPlanning = false;
        ShowRecoveredOfficeProjectOrder(order, project.Id);
        if (project.IsDeleted || order.IsDeleted)
            Error = "Проект был сохранён и сейчас находится в корзине.";
    }

    private void ShowRecoveredOfficeProjectOrder(WorkflowOrder order, Guid? project = null)
    {
        if (!Employees.SequenceEqual(_workspace.Employees)) RefreshEmployees(_connection.EmployeeId);
        RefreshOrders();
        LoadOrder(order, project);
    }
}
