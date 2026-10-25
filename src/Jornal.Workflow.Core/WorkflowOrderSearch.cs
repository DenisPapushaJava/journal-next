using System.Globalization;

namespace Jornal.Workflow.Core;

public sealed class WorkflowSearchQuery(string text)
{
    internal string[] Terms { get; } = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool IsEmpty => Terms.Length == 0;
    public string? OrderReference
    {
        get
        {
            var terms = Terms.AsEnumerable();
            if (Terms.Length > 1 && Terms[0].Equals("Заявка", StringComparison.OrdinalIgnoreCase)) terms = terms.Skip(1);
            var candidate = string.Join("-", terms).Replace(":", "", StringComparison.Ordinal);
            return WorkflowOrderNumbers.IsValid(candidate) ? candidate : null;
        }
    }
}

public sealed record WorkflowSearchField(string Label, string Value, bool IsDetail = false);

public sealed class WorkflowOrderSearchDocument(IReadOnlyList<WorkflowSearchField> fields)
{
    public bool Matches(WorkflowSearchQuery query, out string detail)
    {
        detail = "";
        foreach (var term in query.Terms)
            if (!fields.Any(field => field.Value.Contains(term, StringComparison.OrdinalIgnoreCase))) return false;
        if (!query.IsEmpty)
        {
            var match = fields.FirstOrDefault(field => field.IsDetail &&
                query.Terms.Any(term => field.Value.Contains(term, StringComparison.OrdinalIgnoreCase)));
            if (match is not null) detail = match.Label + ": " + match.Value;
        }
        return true;
    }
}

// Read-side projection of an immutable saved snapshot. No reports, paths or history are read from disk.
public sealed class WorkflowOrderSearchIndex
{
    private readonly Dictionary<Guid, string> _customers;
    private readonly Dictionary<Guid, string> _employees;
    private readonly Dictionary<Guid, string> _machines;
    private readonly Dictionary<string, Guid> _references;
    public WorkflowWorkspace Source { get; }
    public IReadOnlyDictionary<Guid, WorkflowOrderSearchDocument> Documents { get; }

    public WorkflowOrderSearchIndex(WorkflowWorkspace source)
    {
        Source = source;
        _customers = source.Customers.ToDictionary(c => c.Id, c => c.Name);
        _employees = source.Employees.ToDictionary(e => e.Id, e => e.Name);
        _machines = source.Machines.ToDictionary(m => m.Id, m => m.Name);
        _references = source.Orders.Where(o => !o.IsDeleted).ToDictionary(o => o.ReferenceNumber, o => o.Id, StringComparer.Ordinal);
        Documents = source.Orders.Where(o => !o.IsDeleted).ToDictionary(o => o.Id, CreateDocument);
    }

    public Guid? FindExactOrder(WorkflowSearchQuery query) => query.OrderReference is { } reference &&
        _references.TryGetValue(reference, out var id) ? id : null;

    public WorkflowOrderSearchDocument CreateDocument(WorkflowOrder order)
    {
        var fields = new List<WorkflowSearchField>();
        void Add(string label, string? value, bool detail = false)
        { if (!string.IsNullOrWhiteSpace(value)) fields.Add(new(label, value, detail)); }
        void Thickness(decimal thickness)
        {
            Add("Толщина", thickness.ToString("0.############################", CultureInfo.InvariantCulture) + " мм", true);
            if (decimal.Truncate(thickness) != thickness)
                Add("Толщина", thickness.ToString("0.############################", CultureInfo.GetCultureInfo("ru-RU")) + " мм", true);
        }
        Add("Заявка", "Заявка " + order.DisplayNumber);
        var number = order.DisplayNumber;
        if (number.Length >= 17)
        {
            var time = number[11..13] + ":" + number[13..15] + ":" + number[15..17] + number[17..];
            Add("Заявка", number[..10] + " " + time);
            Add("Заявка", number[..10] + "-" + time);
        }
        Add("Дата", order.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        Add("Заказчик", _customers.GetValueOrDefault(order.CustomerId));
        Add("Менеджер", _employees.GetValueOrDefault(order.ManagerId));
        Add("Технолог", _employees.GetValueOrDefault(order.TechnologistId), true);
        if (order.CreatedById is { } creator) Add("Автор", _employees.GetValueOrDefault(creator), true);
        if (order.Blcb is { } blcb)
            Add("БЛЦБ", "БЛЦБ " + blcb);
        foreach (var project in order.ActiveProjects)
        {
            Add("Проект", project.Name, true);
            Add("Станок", _machines.GetValueOrDefault(project.MachineId), true);
            Thickness(project.ThicknessMm);
        }
        var deletedProjects = order.Projects.Where(p => p.IsDeleted).Select(p => p.Id).ToHashSet();
        foreach (var specification in order.Specifications.Where(s => !deletedProjects.Contains(s.Id)))
        {
            var current = specification.Current; var parameters = current.Parameters;
            Add("Спецификация", current.DocumentNumber, true);
            Add("Дата спецификации", current.DocumentDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), true);
            Add("Отчёт", current.Report?.FileName, true);
            Add("Материал", parameters.Material, true);
            Add("Категория", parameters.MaterialGroup, true);
            Add("Станок", _machines.GetValueOrDefault(parameters.MachineId), true);
            Add("Станок", parameters.Machine, true);
            Add("Заготовка", parameters.BlankSize, true);
            Add("Комментарий", parameters.Comment, true);
            Thickness(parameters.ThicknessMm);
        }
        return new(fields);
    }
}
