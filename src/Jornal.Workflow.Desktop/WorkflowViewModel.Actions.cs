using System.Globalization;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    // These checks use the loaded workspace and drafts only. File/network validation stays in the actions.
    private string? CardActionProblem => !CanUse ? "Дождитесь завершения операции." :
        !IsCurrentOrderActive || IsCreating ? "Выберите активную заявку." :
        IsCurrentCardStale || RequiresDatabaseRefresh ? "Обновите карточку и проверьте изменения заявки (F5)." : null;
    private string? AuthorProblem => ActiveEmployee is null ? "Выберите себя: Настройки → Общие → Технолог." : null;
    private string? CardWriteProblem => CardActionProblem ?? AuthorProblem;
    private bool HasHeaderChanges => CurrentOrder is { } order &&
        (!ManagerName.Trim().Equals(EmployeeName(order.ManagerId), StringComparison.OrdinalIgnoreCase) || Technologist?.Id != order.TechnologistId ||
         (string.IsNullOrWhiteSpace(Blcb) ? null : Blcb.Trim()) != order.Blcb);

    public bool CanStartNewOrder => CanUse && !IsCreating && AuthorProblem is null;
    public string NewOrderHint => !CanUse ? "Дождитесь завершения операции." : IsCreating ? "Завершите создание текущей заявки." : AuthorProblem ?? "Создать заявку";
    private string? CreateOrderProblem => !CanUse ? "Дождитесь завершения операции." : AuthorProblem ??
        (string.IsNullOrWhiteSpace(NewManager) ? "Укажите менеджера." : string.IsNullOrWhiteSpace(NewCustomer) ? "Укажите заказчика." : null);
    public bool CanCreateOrder => CreateOrderProblem is null;
    public string CreateOrderHint => CreateOrderProblem ?? "Создать заявку";

    private string? SaveHeaderProblem => CardWriteProblem ?? (string.IsNullOrWhiteSpace(ManagerName) ? "Укажите менеджера." :
        Technologist is null ? "Выберите ответственного технолога." : !HasHeaderChanges ? "Реквизиты не изменены." : null);
    public bool CanSaveHeader => SaveHeaderProblem is null;
    public string SaveHeaderHint => SaveHeaderProblem ?? "Сохранить реквизиты заявки";
    private string? CleanCardProblem => CardWriteProblem ?? (HasCurrentCardEdits ? "Сохраните или отмените изменения карточки." :
        CurrentOrder?.HasProjectRename == true ? "Завершите изменение параметров проекта." : null);
    public bool CanAddProject => CleanCardProblem is null && ProjectMachines.Any();
    public string AddProjectHint => CleanCardProblem ?? (!ProjectMachines.Any() ? "Добавьте станок: Станки и цены." : "Подготовить новый проект");
    private string? CreateProjectProblem =>
        // A created placeholder must remain recoverable after a failed database write.
        _preparedProject is not null ? !CanUse ? "Дождитесь завершения операции." : !IsCurrentOrderActive || RequiresDatabaseRefresh ?
            "Обновите журнал и проверьте сохранение проекта (F5)." : AuthorProblem :
        CardWriteProblem ?? (!IsPlanning ? "Откройте добавление проекта." : PlanMachine is null ? "Выберите станок." :
            NeedsProjectRoot ? "Укажите папку проектов: Настройки → Хранилище." :
            !PositiveNumber(PlanThickness, out _) ? "Укажите положительную толщину проекта." : null);
    public bool CanCreateProject => CreateProjectProblem is null;
    public string CreateProjectHint => CreateProjectProblem ?? (_preparedProject is null ? "Создать папку и заготовку проекта" : "Повторить сохранение подготовленного проекта");
    public bool CanCancelPlanning => CanUse && _preparedProject is null;
    public string CancelPlanningHint => _preparedProject is not null ? "Заготовка уже создана — завершите сохранение проекта." : "Отменить ввод параметров";
    private string? LoadReportsProblem => CardWriteProblem ?? (HasCurrentCardEdits ? "Сохраните или отмените изменения карточки." :
        CurrentOrder?.HasProjectRename == true ? "Завершите изменение параметров проекта." :
        CurrentOrder?.ActiveProjects.Any() != true ? "Сначала добавьте проект в заявку." : null);
    public bool CanLoadReports => LoadReportsProblem is null;
    public string LoadReportsHint => LoadReportsProblem ?? "Найти и проверить спецификации рядом с проектами";

    private string? SaveSpecificationProblem
    {
        get
        {
            if (CardWriteProblem is { } problem) return problem;
            if (CurrentOrder?.HasProjectRename == true) return "Завершите изменение параметров проекта.";
            if (SelectedDraft is not { } draft) return "Выберите расчёт.";
            if (draft.MaterialNeedsReview && !draft.MaterialConfirmed) return "Подтвердите материал спецификации.";
            if (draft.Quote is null) return draft.CalculationError ?? "Проверьте параметры и тариф расчёта.";
            if (string.IsNullOrWhiteSpace(draft.Number)) return "Укажите номер спецификации.";
            if (!DateOnly.TryParseExact(draft.Date, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || date == default)
                return "Укажите дату спецификации в формате ДД.ММ.ГГГГ.";
            return !draft.IsDirty ? "Расчёт не изменён." : null;
        }
    }
    public bool CanSaveSpecification => SaveSpecificationProblem is null;
    public string SaveSpecificationHint => SaveSpecificationProblem ?? "Сохранить расчёт";
    public bool CanCancelDraft => CanUse && SelectedDraft?.IsDirty == true;
    public string CancelDraftHint => !CanUse ? "Дождитесь завершения операции." :
        SelectedDraft?.IsDirty == true ? "Отменить правки выбранного расчёта" : "В расчёте нет несохранённых изменений.";
    public bool CanRefreshRate => CanUse && SelectedDraft is { } draft && PositiveNumber(draft.Thickness, out var thickness) &&
        _workspace.Rates.Any(r => r.MachineId == draft.MachineId && r.MaterialGroup == draft.MaterialGroup && r.ThicknessMm == thickness);
    public string RefreshRateHint => CanRefreshRate ? "Обновить цены расчёта из каталога" : !CanUse ? "Дождитесь завершения операции." : "Для этих параметров нет тарифа. Откройте «Станки и цены».";
    public bool CanMoveOrderToTrash => CardWriteProblem is null && !HasCurrentCardEdits;
    public string MoveOrderToTrashHint => CardWriteProblem ?? (HasCurrentCardEdits ? "Сохраните или отмените изменения карточки." : "Переместить заявку в корзину с возможностью восстановления");

    private static bool PositiveNumber(string? text, out decimal number) =>
        decimal.TryParse(text?.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out number) && number > 0;
    private void NotifyNewOrderActions()
    {
        OnPropertyChanged(nameof(CanStartNewOrder)); OnPropertyChanged(nameof(NewOrderHint));
        OnPropertyChanged(nameof(CanCreateOrder)); OnPropertyChanged(nameof(CreateOrderHint));
        NewOrderCommand.NotifyCanExecuteChanged(); CreateOrderCommand.NotifyCanExecuteChanged();
    }
    private void NotifyPlanActions()
    {
        foreach (var name in new[] { nameof(CanCreateProject), nameof(CreateProjectHint), nameof(CanCancelPlanning), nameof(CancelPlanningHint) }) OnPropertyChanged(name);
        CreateProjectCommand.NotifyCanExecuteChanged(); CancelPlanningCommand.NotifyCanExecuteChanged();
    }
    private void NotifyActionAvailability()
    {
        NotifyNewOrderActions(); NotifyPlanActions(); NotifyResultState();
        foreach (var name in new[] { nameof(CanSaveHeader), nameof(SaveHeaderHint), nameof(CanAddProject), nameof(AddProjectHint),
            nameof(CanLoadReports), nameof(LoadReportsHint), nameof(CanSaveSpecification), nameof(SaveSpecificationHint),
            nameof(CanCancelDraft), nameof(CancelDraftHint), nameof(CanRefreshRate), nameof(RefreshRateHint),
            nameof(CanMoveOrderToTrash), nameof(MoveOrderToTrashHint) }) OnPropertyChanged(name);
        SaveHeaderCommand.NotifyCanExecuteChanged(); AddProjectCommand.NotifyCanExecuteChanged();
        SaveSpecificationCommand.NotifyCanExecuteChanged(); CancelDraftCommand.NotifyCanExecuteChanged(); RefreshRateCommand.NotifyCanExecuteChanged();
    }
}
