using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowProjectRow(CuttingProject Project, int ItemNumber, string Machine, bool IsCurrent,
    SpecificationRevision? Revision)
{
    public string Position => $"{ItemNumber} · вариант {Project.Variant}";
    public string Name => Project.Name;
    public string Material => Revision?.Parameters.Material ?? "—";
    public string Thickness => SpecificationDraft.N(Project.ThicknessMm);
    public string State => Revision is null ? "Ожидает спецификацию" : IsCurrent ? "В итоге" : "Расчёт готов · вне итога";
    public string Total => Revision is null ? "—" : SpecificationDraft.Money(Revision.Calculation.TotalCost) + " ₽";
    public string? FolderPath => Path.GetDirectoryName(Project.NativePath ?? Revision?.SourcePath);
    public bool HasFolder => !string.IsNullOrWhiteSpace(FolderPath);
    public string FolderHint => HasFolder ? "Открыть папку проекта" : "Папка проекта не задана";
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
    public bool NeedsProjectRoot => string.IsNullOrWhiteSpace(_workspace.ProjectRoot);
    [ObservableProperty] private WorkflowProjectRow? _selectedProject;
    [ObservableProperty] private bool _isPlanning;
    [ObservableProperty] private WorkflowMachine? _planMachine;
    [ObservableProperty] private string _planThickness = "";
    [ObservableProperty] private bool _resultIsStale;
    [ObservableProperty] private bool _isResultExpanded;
    [ObservableProperty] private int _orderTabIndex;
    partial void OnOrderTabIndexChanged(int value) => IsResultExpanded = value == 3;
    partial void OnIsResultExpandedChanged(bool value)
    {
        if (value) OrderTabIndex = 3;
        else if (OrderTabIndex == 3) OrderTabIndex = 0;
    }
    partial void OnResultTextChanged(string value) { if (value.Length > 0) IsResultExpanded = true; }
    private Guid? _redoItem;
    private CuttingProject? _preparedProject;
    public bool CanEditPlan => _preparedProject is null;
    public string PlanTitle => _redoItem is null ? "Новый проект" : "Другой вариант позиции";
    public string PlannedName => PlanMachine is null ? "" : WorkflowProjectNames.Tag(PlanMachine) + " · " + WorkflowProjectNames.Extension(PlanMachine) + " · время ЧЧММСС";
    public string ProjectsStatus => CurrentOrder is not { } order ? "" :
        $"{order.ActiveSpecifications.Count()} рассчитано · {order.PendingProjectCount} ожидает спецификацию" +
        (HasUnselectedVariant ? " · есть переделки: выберите вариант для итога" : "");
    private bool HasUnselectedVariant => CurrentOrder is { } order && order.Items.Any(item =>
        order.Projects.Any(p => p.ItemId == item.Id && p.Variant > item.ReviewedThroughVariant));
    [RelayCommand] private void AddProject()
    {
        if (!CanUse || !IsCurrentOrderActive) return;
        if (HasCurrentCardEdits) { Error = "Сохраните или отмените текущие изменения."; return; }
        _redoItem = null; PlanMachine ??= _workspace.Machines.FirstOrDefault();
        IsPlanning = true; OnPropertyChanged(nameof(PlanTitle)); Error = null;
    }
    [RelayCommand] private void RedoProject()
    {
        if (!CanUse || !IsCurrentOrderActive || SelectedProject is not { } row) return;
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
        if (!CanUse || CurrentOrder is not { IsDeleted: false } order) return;
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
            var latest = await _store.ReadAsync();
            var target = latest.Orders.Single(o => o.Id == order.Id);
            var project = _preparedProject;
            if (!target.Projects.Any(p => p.Id == project.Id))
                project = _preparedProject = project with { Variant = target.Projects.Where(p => p.ItemId == project.ItemId).Select(p => p.Variant).DefaultIfEmpty().Max() + 1 };
            _workspace = await _store.WriteOrderAsync(order.Id, target.Version, book =>
                WorkflowProjects.Add(book, order.Id, target.Version, project, actor, DateTimeOffset.UtcNow));
            if (target.Version == CardVersion) _loadedOrderVersion = CurrentOrder!.Version;
            _preparedProject = null; IsPlanning = false; OnPropertyChanged(nameof(CanEditPlan));
            RefreshOrders(); RefreshProjects(project.Id); RefreshHistory(); NotifyOrder();
        });
    }
    [RelayCommand] private async Task SelectProjectForTotalAsync()
    {
        if (!CanUse || CurrentOrder is not { IsDeleted: false } order || SelectedProject is not { } row) return;
        if (HasCurrentCardEdits) { Error = "Сохраните или отмените изменения перед выбором варианта."; return; }
        await RunAsync(async () =>
        {
            var actor = ActorId();
            var expected = CardVersion;
            _workspace = await _store.WriteOrderAsync(order.Id, expected, book =>
                WorkflowProjects.Select(book, order.Id, expected, row.Project.Id, actor, DateTimeOffset.UtcNow));
            _loadedOrderVersion = CurrentOrder!.Version;
            MarkResultStale(); RefreshProjects(row.Project.Id); RefreshOrders(); RefreshHistory(); NotifyOrder();
        });
    }
    partial void OnSelectedProjectChanged(WorkflowProjectRow? value)
    {
        if (value is not null) SelectedDraft = Drafts.SingleOrDefault(d => d.Id == value.Project.Id);
    }
    partial void OnPlanMachineChanged(WorkflowMachine? value)
    {
        OnPropertyChanged(nameof(PlannedName));
        OnPropertyChanged(nameof(PlanThicknessSuggestions));
    }
    partial void OnIsPlanningChanged(bool value) => OnPropertyChanged(nameof(HasPendingEdits));
    private void MarkResultStale() { if (!string.IsNullOrWhiteSpace(ResultText)) ResultIsStale = true; }
    private void RefreshProjects(Guid? select = null)
    {
        var id = select ?? SelectedProject?.Project.Id; Projects.Clear();
        if (CurrentOrder is { } order)
            foreach (var project in order.Projects.OrderBy(p => order.Items.Single(i => i.Id == p.ItemId).Number).ThenBy(p => p.Variant))
            {
                var item = order.Items.Single(i => i.Id == project.ItemId);
                Projects.Add(new(project, item.Number, _workspace.Machines.Single(m => m.Id == project.MachineId).Name,
                    item.CurrentProjectId == project.Id, order.Specifications.SingleOrDefault(s => s.Id == project.Id)?.Current));
            }
        SelectedProject = Projects.SingleOrDefault(p => p.Project.Id == id) ?? Projects.FirstOrDefault();
        OnPropertyChanged(nameof(ProjectMachines)); OnPropertyChanged(nameof(ProjectsStatus));
        OnPropertyChanged(nameof(IsProjectListEmpty)); OnPropertyChanged(nameof(NeedsProjectRoot));
        OnPropertyChanged(nameof(PlanThicknessSuggestions));
        OnPropertyChanged(nameof(CustomerSuggestions));
        NotifyProfile();
    }
}
