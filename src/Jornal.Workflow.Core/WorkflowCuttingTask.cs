using System.Globalization;
using System.Text;

namespace Jornal.Workflow.Core;

public enum CuttingTaskTarget { None, Header, Project, Projects, Refresh, Reports }
public sealed record CuttingTaskProblem(string Message, CuttingTaskTarget Target, Guid? ProjectId = null);
public sealed record CuttingTaskPreview(string Text, CuttingTaskProblem? Problem = null)
{
    public bool IsReady => Problem is null && Text.Length > 0;
}

// A message describes saved work only. It does not claim that any files reached the workshop.
public static class WorkflowCuttingTask
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");

    public static CuttingTaskPreview Prepare(WorkflowOrder order, IReadOnlyList<Employee> employees,
        IReadOnlyList<Customer> customers, IReadOnlyList<WorkflowMachine> machines)
    {
        static CuttingTaskPreview Block(string message, CuttingTaskTarget target, Guid? id = null) => new("", new(message, target, id));
        if (order.IsDeleted) return Block("Заявка находится в корзине.", CuttingTaskTarget.None);
        if (order.HasProjectRename)
            return Block("Завершите изменение параметров проекта.", CuttingTaskTarget.Project, order.Projects.First(p => p.Rename is not null).Id);
        if (Line(order.Blcb).Length == 0) return Block("Укажите БЛЦБ для задания на резку.", CuttingTaskTarget.Header);
        var manager = Line(employees.SingleOrDefault(e => e.Id == order.ManagerId)?.Name);
        var customer = Line(customers.SingleOrDefault(c => c.Id == order.CustomerId)?.Name);
        if (manager.Length == 0 || customer.Length == 0)
            return Block("Проверьте менеджера и заказчика заявки.", CuttingTaskTarget.Header);

        var projects = order.ActiveProjects.ToLookup(p => p.ItemId);
        var saved = order.Specifications.ToDictionary(s => s.Id);
        foreach (var item in order.Items.Where(i => i.IsIncluded).OrderBy(i => i.Number))
        {
            var variants = projects[item.Id].OrderByDescending(p => p.Variant).ToArray();
            if (variants.Length == 0) continue;
            var current = variants.SingleOrDefault(p => p.Id == item.CurrentProjectId);
            if (current is null || variants[0].Variant > item.ReviewedThroughVariant)
                return Block($"Позиция {item.Number}: проверьте и выберите вариант для задания.", CuttingTaskTarget.Project, variants[0].Id);
            if (!saved.ContainsKey(current.Id))
                return Block($"Позиция {item.Number}: загрузите спецификацию и сохраните расчёт.", CuttingTaskTarget.Project, current.Id);
        }

        var specifications = order.ActiveSpecifications.ToArray();
        if (specifications.Length == 0) return Block("Добавьте или включите проекты в состав заявки и сохраните расчёты.", CuttingTaskTarget.Projects);
        var names = machines.ToDictionary(m => m.Id, m => Line(m.Name));
        foreach (var spec in specifications)
        {
            var p = spec.Current.Parameters;
            if (p.MaterialNeedsReview || Line(p.Material).Length == 0 || p.ThicknessMm <= 0 ||
                !names.TryGetValue(p.MachineId, out var name) || name.Length == 0)
                return Block("Проверьте материал, толщину и станок спецификации.", CuttingTaskTarget.Project, spec.Id);
        }

        var text = new StringBuilder().Append(ShortNumber(Line(order.Blcb))).Append("  ")
            .Append(manager).Append(" (").Append(customer).Append(")\n");
        var machineOrder = machines.Select((m, index) => (m.Id, index)).ToDictionary(x => x.Id, x => x.index);
        var groups = specifications.GroupBy(s => s.Current.Parameters.MachineId).OrderBy(g => machineOrder[g.Key]).ToArray();
        for (var index = 0; index < groups.Length; index++)
        {
            if (index > 0) text.Append('\n');
            var group = groups[index];
            var rows = group.OrderBy(s => s.Current.Parameters.ThicknessMm)
                .ThenBy(s => s.Current.Parameters.Material, StringComparer.Create(Culture, true)).ThenBy(s => s.Id).ToArray();
            for (var i = 0; i < rows.Length; i++)
            {
                var p = rows[i].Current.Parameters;
                text.Append(p.ThicknessMm.ToString("0.############################", Culture)).Append(" мм ").Append(Line(p.Material));
                if (p.HasBending) text.Append(" ГИБКА");
                if (p.HasChamfer) text.Append(p.HasBending ? ", ФАСКА" : " ФАСКА");
                if (i == rows.Length - 1) text.Append("   на ").Append(names[group.Key] switch
                { "Плазма" => "Плазму", "Газо-кислородная" => "Кристалл резка газом", var name => name });
                text.Append('\n');
            }
        }
        return new(text.ToString());
    }

    private static string Line(string? value) => string.Join(" ", new string((value ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray())
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string ShortNumber(string value)
    {
        var suffix = value.Length > 5 ? value[^5..] : value;
        return suffix.All(char.IsAsciiDigit) ? suffix.TrimStart('0') is { Length: > 0 } number ? number : "0" : value;
    }
}
