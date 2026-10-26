using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public sealed class WorkflowProjectRow(CuttingProject project, int itemNumber, string machine, bool isCurrent,
    SpecificationState? saved, bool included = true, bool legacyCalculation = false, bool hasAlternatives = false,
    bool hasLocalChanges = false) : ObservableObject
{
    public CuttingProject Project { get; } = project;
    public int ItemNumber { get; } = itemNumber;
    public string Machine { get; } = machine;
    public bool IsCurrent { get; } = isCurrent;
    public SpecificationState? Saved { get; } = saved;
    public bool IsLegacyCalculation { get; } = legacyCalculation;
    public bool HasProjectActions => !IsLegacyCalculation;
    public bool CanChooseForTotal => HasProjectActions && !IsDeleted && Saved is not null && hasAlternatives;
    private bool _hasLocalChanges = hasLocalChanges;
    public bool HasLocalChanges => _hasLocalChanges;
    internal void ApplyLocalChanges(bool value)
    { if (SetProperty(ref _hasLocalChanges, value, nameof(HasLocalChanges))) { OnPropertyChanged(nameof(State)); OnPropertyChanged(nameof(DetailsHint)); } }
    private bool _included = included;
    private bool _inclusionWritePending;
    private bool? _pendingIncluded;
    public bool Included => _included;
    public bool DisplayedIncluded => _pendingIncluded ?? IsIncluded;
    public bool CanHitToggle => CanToggle && !_inclusionWritePending;
    internal void ApplyIncluded(bool value)
    {
        if (!SetProperty(ref _included, value, nameof(Included))) return;
        foreach (var name in new[] { nameof(IsIncluded), nameof(DisplayedIncluded), nameof(IsExcluded), nameof(State), nameof(DetailsHint) }) OnPropertyChanged(name);
    }
    internal void SetInclusionWritePending(bool value, bool? requested = null)
    {
        _inclusionWritePending = value; _pendingIncluded = value ? requested : null;
        OnPropertyChanged(nameof(CanHitToggle)); OnPropertyChanged(nameof(DisplayedIncluded));
    }
    public string Position => IsLegacyCalculation ? "Без проекта" : $"{ItemNumber}";
    public string VariantCaption => hasAlternatives ? $"Вариант {Project.Variant}" : "";
    public bool HasAlternatives => hasAlternatives;
    public string Name => Project.Name;
    public string Material => Saved?.Parameters.Material ?? "—";
    public string Thickness => SpecificationDraft.N(Project.ThicknessMm);
    public bool IsDeleted => Project.IsDeleted;
    public bool IsActive => !IsDeleted;
    public bool IsIncluded => Included && !IsDeleted;
    public bool IsExcluded => !Included && !IsDeleted;
    public bool CanToggle => HasProjectActions && !IsDeleted && Project.Rename is null;
    public bool CanEdit => !IsDeleted && Saved is null && Project.NativePath is not null;
    public bool CanTrash => HasProjectActions && !IsDeleted && Project.Rename is null;
    public string EditCaption => Project.Rename is null ? "Изменить параметры…" : "Завершить изменение…";
    public string InclusionHint => IsLegacyCalculation ? "Ранее сохранённый расчёт без проекта" : $"Учитывать позицию {ItemNumber} в сумме, письме и комплектности заявки. Отметка общая для её вариантов.";
    public string State => IsDeleted ? "В корзине · файлы сохранены" : Project.Rename is not null ? "Изменение не завершено · откройте параметры" :
        !Included ? "Исключён из состава заявки" : HasLocalChanges ? "Изменения не сохранены" : Saved is null ? "Нет спецификации" : IsCurrent ? "Рассчитан" : "Другой вариант · вне итога";
    public string Total => Saved is null ? "—" : SpecificationDraft.Money(Saved.Calculation.TotalCost) + " ₽";
    public string ChamferMark => Saved is null ? "—" : Saved.Parameters.HasChamfer ? "✓" : "";
    public string BendingMark => Saved is null ? "—" : Saved.Parameters.HasBending ? "✓" : "";
    public string ChamferHint => Saved is null ? "Фаска: спецификация ещё не загружена" : Saved.Parameters.HasChamfer ? "Фаска отмечена" : "Фаска не отмечена";
    public string BendingHint => Saved is null ? "Гибка: спецификация ещё не загружена" : Saved.Parameters.HasBending ? "Гибка отмечена" : "Гибка не отмечена";
    public string? FolderPath => Path.GetDirectoryName(Project.NativePath ?? Saved?.SourcePath);
    public bool HasFolder => !string.IsNullOrWhiteSpace(FolderPath);
    public string FolderHint => HasFolder ? "Открыть папку проекта" : "Папка проекта не задана";
    public string DetailsHint => Name + "\nМатериал: " + Material + "\n" + State;
}

public partial class WorkflowViewModel
{
    public ObservableCollection<WorkflowProjectRow> Projects { get; } = [];
    public IReadOnlyList<WorkflowMachine> ProjectMachines => _workspace.Machines;
    public IReadOnlyList<string> CustomerSuggestions => _localPreferences.EntrySuggestions?.Customers ?? [];
    public IReadOnlyList<string> ManagerSuggestions => _localPreferences.EntrySuggestions?.Managers ?? [];
    public IReadOnlyList<string> PlanThicknessSuggestions => _workspace.Rates.Where(r => r.MachineId == PlanMachine?.Id)
        .Select(r => r.ThicknessMm).Distinct().Order().Select(SpecificationDraft.N).ToArray();
    public bool IsProjectListEmpty => Projects.Count == 0;
    [ObservableProperty] private bool _showTrashedProjects;
    public string ProjectTrashCaption => $"Корзина проектов ({CurrentOrder?.Projects.Count(p => p.IsDeleted) ?? 0})";
    public string EmptyProjectText => ShowTrashedProjects ? "В корзине нет проектов." : "Добавьте проект: станок и толщина.";
    public bool ShowProjectCalculation => Projects.Count > 0 && !IsPlanning && !ShowTrashedProjects;
    public string CalculationEmptyText => SelectedProject is null ? "Выберите проект." :
        "Сохраните HTML/PDF-спецификацию рядом с файлом проекта, затем нажмите «Загрузить спецификации».";
    public double CalculationMaxHeight => !ShowProjectCalculation ? 0 : HasSelectedDraft ? double.PositiveInfinity : 88;
    private void NotifyProjectCalculation()
    { OnPropertyChanged(nameof(ShowProjectCalculation)); OnPropertyChanged(nameof(CalculationMaxHeight)); OnPropertyChanged(nameof(CalculationEmptyText)); }
    partial void OnShowTrashedProjectsChanged(bool value) { RefreshProjects(); OnPropertyChanged(nameof(EmptyProjectText)); NotifyProjectCalculation(); }
    public bool NeedsProjectRoot => string.IsNullOrWhiteSpace(_workspace.ProjectRoot);
    [ObservableProperty] private WorkflowProjectRow? _selectedProject;
    private bool _refreshingProjects;
    [ObservableProperty] private bool _isPlanning;
    [ObservableProperty] private WorkflowMachine? _planMachine;
    [ObservableProperty] private string _planThickness = "";
    [ObservableProperty] private bool _resultIsStale;
    [ObservableProperty] private bool _isResultExpanded;
    [ObservableProperty] private int _orderTabIndex;
    partial void OnOrderTabIndexChanged(int value) => IsResultExpanded = value == 1;
    partial void OnIsResultExpandedChanged(bool value)
    {
        if (value) OrderTabIndex = 1;
        else if (OrderTabIndex == 1) OrderTabIndex = 0;
    }
    partial void OnResultTextChanged(string value) { if (value.Length > 0) IsResultExpanded = true; }
    private Guid? _redoItem;
    private CuttingProject? _preparedProject;
    public bool CanEditPlan => _preparedProject is null;
    public string PlanTitle => _redoItem is null ? "Новый проект" : "Другой вариант позиции";
    public string PlannedName => PlanMachine is null ? "" : WorkflowProjectNames.Tag(PlanMachine) + " · " + WorkflowProjectNames.Extension(PlanMachine) + " · время ЧЧММСС";
    public string ProjectsStatus => CurrentOrder is not { } order ? "" :
        $"{order.ActiveSpecifications.Count()} рассчитано · {order.PendingProjectCount} ожидает спецификацию" +
        (HasUnselectedVariant ? " · выберите актуальные варианты" : "") +
        (order.ActiveSpecifications.Count(s => s.Current.Parameters.HasBending) is > 0 and var bending ? $" · гибка: {bending} (цена отдельно)" : "") +
        (order.Items.Count(i => !i.IsIncluded) is > 0 and var excluded ? $" · исключено позиций: {excluded}" : "");
    private bool HasUnselectedVariant => CurrentOrder is { } order && order.Items.Any(item => item.IsIncluded &&
        order.ActiveProjects.Any(p => p.ItemId == item.Id) &&
        (!order.ActiveProjects.Any(p => p.Id == item.CurrentProjectId) ||
         order.ActiveProjects.Any(p => p.ItemId == item.Id && p.Variant > item.ReviewedThroughVariant)));
    [RelayCommand] private void AddProject()
    {
        if (!CanUse || !IsCurrentOrderActive) return;
        if (CurrentOrder?.HasProjectRename == true) { Error = "Сначала завершите изменение параметров проекта."; return; }
        if (HasCurrentCardEdits) { Error = "Сохраните или отмените текущие изменения."; return; }
        _redoItem = null; PlanMachine ??= _workspace.Machines.FirstOrDefault();
        IsPlanning = true; OnPropertyChanged(nameof(PlanTitle)); Error = null;
    }
    [RelayCommand] private void RedoProject()
    {
        if (!CanUse || !IsCurrentOrderActive || SelectedProject is not { IsDeleted: false, IsLegacyCalculation: false } row) return;
        if (CurrentOrder?.HasProjectRename == true) { Error = "Сначала завершите изменение параметров проекта."; return; }
        if (HasCurrentCardEdits) { Error = "Сохраните или отмените текущие изменения."; return; }
        _redoItem = row.Project.ItemId; PlanMachine = _workspace.Machines.Single(m => m.Id == row.Project.MachineId);
        PlanThickness = row.Thickness;
        IsPlanning = true; OnPropertyChanged(nameof(PlanTitle)); Error = null;
    }
    [RelayCommand] private void CancelPlanning()
    {
        if (!CanUse) return;
        if (_preparedProject is { } prepared)
        { Error = "Заготовка уже создана: " + prepared.NativePath + ". Повторите сохранение проекта."; return; }
        IsPlanning = false;
    }
    [RelayCommand] private async Task CreateProjectAsync()
    {
        if (!CanUse || !IsCurrentOrderActive || CurrentOrder is not { IsDeleted: false } order) return;
        await RunAsync(async () =>
        {
            var actor = ActorId();
            if (_preparedProject is null)
            {
                var machine = PlanMachine ?? throw new WorkflowValidationException("Выберите станок.");
                var root = _workspace.ProjectRoot ?? throw new WorkflowValidationException("Выберите общую папку проектов в настройках.");
                var thickness = SpecificationDraft.Decimal(PlanThickness, "толщину");
                var item = _redoItem;
                _preparedProject = await Task.Run(() => WorkflowProjectFiles.PrepareAsync(root, order,
                    CustomerName(order.CustomerId), machine, thickness, item, DateTimeOffset.Now));
                OnPropertyChanged(nameof(CanEditPlan));
            }
            var latest = await _store.ReadOrderAsync(order.Id);
            var target = latest.Orders.Single(o => o.Id == order.Id);
            var project = _preparedProject;
            if (!target.Projects.Any(p => p.Id == project.Id))
                project = _preparedProject = project with { Variant = target.Projects.Where(p => p.ItemId == project.ItemId).Select(p => p.Variant).DefaultIfEmpty().Max() + 1 };
            _workspace = await _store.WriteOrderAsync(order.Id, target.Version, book =>
                WorkflowProjects.Add(book, order.Id, target.Version, project, actor, DateTimeOffset.UtcNow));
            if (target.Version == CardVersion) _loadedOrderVersion = CurrentOrder!.Version;
            _preparedProject = null; IsPlanning = false; OnPropertyChanged(nameof(CanEditPlan));
            RefreshOrders(); RefreshProjects(project.Id); NotifyOrder();
        });
    }
    [RelayCommand] private async Task SelectProjectForTotalAsync()
    {
        if (!CanUse || CurrentOrder is not { IsDeleted: false } order || SelectedProject is not { IsDeleted: false, IsLegacyCalculation: false } row) return;
        if (HasCurrentCardEdits) { Error = "Сохраните или отмените изменения перед выбором варианта."; return; }
        await RunAsync(async () =>
        {
            var actor = ActorId();
            var expected = CardVersion;
            _workspace = await _store.WriteOrderAsync(order.Id, expected, book =>
                WorkflowProjects.Select(book, order.Id, expected, row.Project.Id, actor, DateTimeOffset.UtcNow));
            _loadedOrderVersion = CurrentOrder!.Version;
            MarkResultStale(); RefreshProjects(row.Project.Id); RefreshOrders(); NotifyOrder();
        });
    }
    partial void OnSelectedProjectChanged(WorkflowProjectRow? value)
    {
        if (!_refreshingProjects) SelectedDraft = value is null || value.IsDeleted ? null : Drafts.SingleOrDefault(d => d.Id == value.Project.Id);
        NotifyProjectCalculation();
    }
    partial void OnPlanMachineChanged(WorkflowMachine? value)
    {
        OnPropertyChanged(nameof(PlannedName));
        OnPropertyChanged(nameof(PlanThicknessSuggestions));
    }
    partial void OnIsPlanningChanged(bool value) { OnPropertyChanged(nameof(HasPendingEdits)); NotifyProjectCalculation(); NotifyCuttingTask(); }
    private void MarkResultStale() { if (!string.IsNullOrWhiteSpace(ResultText)) ResultIsStale = true; }
    private void RefreshProjects(Guid? select = null)
    {
        var id = select ?? SelectedDraft?.Id ?? SelectedProject?.Project.Id;
        _refreshingProjects = true;
        try
        {
            Projects.Clear();
            if (CurrentOrder is { } order)
            {
                foreach (var project in order.Projects.Where(p => p.IsDeleted == ShowTrashedProjects).OrderBy(p => order.Items.Single(i => i.Id == p.ItemId).Number).ThenBy(p => p.Variant))
                {
                    var item = order.Items.Single(i => i.Id == project.ItemId);
                    Projects.Add(new(project, item.Number, _workspace.Machines.SingleOrDefault(m => m.Id == project.MachineId)?.Name ??
                        order.Specifications.SingleOrDefault(s => s.Id == project.Id)?.Current.Parameters.Machine ?? "Станок удалён",
                        !project.IsDeleted && item.CurrentProjectId == project.Id, order.Specifications.SingleOrDefault(s => s.Id == project.Id)?.Current, item.IsIncluded,
                        hasAlternatives: order.ActiveProjects.Count(p => p.ItemId == project.ItemId) > 1,
                        hasLocalChanges: Drafts.SingleOrDefault(d => d.Id == project.Id)?.IsDirty == true));
                }
                if (!ShowTrashedProjects)
                    foreach (var draft in Drafts.Where(d => !order.Projects.Any(p => p.Id == d.Id)))
                    {
                        var saved = draft.Saved;
                        var project = new CuttingProject(draft.Id, Guid.Empty, 1, draft.MachineId,
                            saved?.Parameters.ThicknessMm ?? 0, draft.Caption, null, draft.ImportProfile, saved?.SavedAt ?? order.CreatedAt);
                        Projects.Add(new(project, 0, draft.Machine, true, saved, legacyCalculation: true, hasLocalChanges: draft.IsDirty));
                    }
            }
            SelectedProject = Projects.SingleOrDefault(p => p.Project.Id == id) ?? Projects.FirstOrDefault();
        }
        finally { _refreshingProjects = false; }
        SelectedDraft = SelectedProject is { IsDeleted: false } selected ? Drafts.SingleOrDefault(d => d.Id == selected.Project.Id) : null;
        OnPropertyChanged(nameof(ProjectMachines)); OnPropertyChanged(nameof(ProjectsStatus)); NotifyCompleteness();
        OnPropertyChanged(nameof(IsProjectListEmpty)); OnPropertyChanged(nameof(NeedsProjectRoot));
        OnPropertyChanged(nameof(ProjectTrashCaption)); OnPropertyChanged(nameof(HasPendingEdits));
        NotifyProjectCalculation();
        OnPropertyChanged(nameof(PlanThicknessSuggestions));
        OnPropertyChanged(nameof(CustomerSuggestions));
        NotifyProfile();
    }
}
