using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfflineJournalSnapshot(int Format, Guid DatabaseId, Guid CredentialId, string Address,
    DateTimeOffset SavedAt, WorkflowWorkspace Workspace);

public sealed record WorkflowOfflineProjectRow(string Name, string Details, string Result)
{
    internal WorkflowOfflineFolder? Folder { get; init; }
    public bool CanOpenFolder => Folder is not null;
}

public sealed record WorkflowOfflineOrderRow(Guid Id, string Number, string Customer, string Details, string Total,
    WorkflowOfflineProjectRow[] Projects)
{
    public bool HasDownloadedProject => Projects.Any(project => project.CanOpenFolder);
}

public sealed partial class WorkflowOfflineJournal : ObservableObject
{
    private readonly WorkflowOfflineOrderRow[] _orders;
    private readonly WorkflowOrderSearchIndex _index;
    private readonly Dictionary<Guid, string> _pendingCalculations = [];
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private WorkflowOfflineOrderRow? _selected;
    public WorkflowOfflineOrderRow[] Orders { get; private set; }
    public string Status { get; }
    public bool HasOrders => Orders.Length > 0;
    public string? SelectedPendingCalculation => Selected is { } row ? _pendingCalculations.GetValueOrDefault(row.Id) : null;
    public bool HasSelectedPendingCalculation => SelectedPendingCalculation is not null;
    internal WorkflowWorkspace Workspace => _index.Source;
    public string EmptyListText => _orders.Length == 0 ? "Сохранённых заявок пока нет" : "Среди сохранённых заявок совпадений нет";
    internal bool HasTechnologist(Guid id) => _index.Source.Employees.Any(employee => employee.Id == id && employee.CanWork);

    internal WorkflowOfflineJournal(WorkflowOfflineJournalSnapshot snapshot, WorkflowOfflineFolders folders, WorkflowOfflineJournal? previous = null)
    {
        if (previous is not null) _pendingCalculations = new(previous._pendingCalculations);
        var workspace = snapshot.Workspace;
        _index = new(workspace);
        var people = workspace.Employees.ToDictionary(person => person.Id, person => person.Name);
        var customers = workspace.Customers.ToDictionary(customer => customer.Id, customer => customer.Name);
        var machines = workspace.Machines.ToDictionary(machine => machine.Id, machine => machine.Name);
        var downloaded = folders.Items.ToDictionary(folder => folder.ProjectId);
        _orders = workspace.Orders.OrderByDescending(order => order.UpdatedAt).ThenByDescending(order => order.Number).Select(order =>
        {
            var details = $"Менеджер {people[order.ManagerId]} · Технолог {people[order.TechnologistId]}";
            if (!string.IsNullOrWhiteSpace(order.Blcb)) details += " · БЛЦБ " + order.Blcb;
            var specs = order.ActiveSpecifications.ToDictionary(spec => spec.Id, spec => spec.Current);
            var projects = order.ActiveProjects.Select(project =>
            {
                var state = specs.GetValueOrDefault(project.Id);
                var caption = $"{machines[project.MachineId]} · {SpecificationDraft.N(project.ThicknessMm)} мм";
                if (state is not null) caption += " · " + state.Parameters.Material;
                var result = state is not null ? SpecificationDraft.Money(state.Calculation.TotalCost) + " ₽" : "Резка не рассчитана";
                return new WorkflowOfflineProjectRow(project.Name, caption, result) { Folder = downloaded.GetValueOrDefault(project.Id) };
            }).ToArray();
            return new WorkflowOfflineOrderRow(order.Id, order.DisplayNumber, customers[order.CustomerId], details,
                specs.Count > 0 ? SpecificationDraft.Money(order.CuttingTotal) + " ₽" : "—", projects);
        }).ToArray();
        Orders = _orders;
        Status = $"Офлайн · данные от {snapshot.SavedAt.ToLocalTime():dd.MM.yyyy HH:mm}";
    }

    partial void OnSearchChanged(string value)
    {
        var query = new WorkflowSearchQuery(value);
        Orders = query.IsEmpty ? _orders : _index.FindExactOrder(query) is { } id ? _orders.Where(order => order.Id == id).ToArray() :
            _orders.Where(order => _index.Documents[order.Id].Matches(query, out _)).ToArray();
        if (Selected is { } selected && !Orders.Contains(selected)) Selected = null;
        OnPropertyChanged(nameof(Orders));
        OnPropertyChanged(nameof(HasOrders));
    }

    internal void ShowPendingCalculation(WorkflowOfficeCalculationDraft draft)
    {
        _pendingCalculations[draft.OrderId] = $"К отправке · {SpecificationDraft.Money(draft.PendingOfficeTotal())} ₽ по готовым проектам";
        if (Selected?.Id == draft.OrderId) OnSelectedChanged(Selected);
    }

    partial void OnSelectedChanged(WorkflowOfflineOrderRow? value)
    {
        OnPropertyChanged(nameof(SelectedPendingCalculation));
        OnPropertyChanged(nameof(HasSelectedPendingCalculation));
    }
}
