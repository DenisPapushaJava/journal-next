using System.Globalization;
using System.Text.RegularExpressions;

namespace Jornal.Workflow.Core;

// The familiar «Комментарий строки». Formatting is shared by all map-signing entry points.
public static class WorkflowProjectComment
{
    public static string Format(WorkflowOrder order, CuttingProject project, IReadOnlyList<Employee> employees, IReadOnlyList<Customer> customers)
    {
        if (order.IsDeleted || project.IsDeleted || project.Rename is not null || !order.Projects.Contains(project))
            throw new WorkflowValidationException("Проект недоступен или его переименование не завершено.");
        var specification = order.Specifications.SingleOrDefault(s => s.Id == project.Id)?.Current
            ?? throw new WorkflowValidationException("Сначала загрузите и сохраните спецификацию проекта — для подписи нужен её номер.");
        var number = Required(specification.DocumentNumber, "номер спецификации");
        var manager = Required(employees.SingleOrDefault(e => e.Id == order.ManagerId)?.Name, "менеджера");
        var customer = Required(customers.SingleOrDefault(c => c.Id == order.CustomerId)?.Name, "заказчика");
        var blcb = Required(order.Blcb, "БЛЦБ заявки");
        if (!blcb.StartsWith("БЛЦБ-", StringComparison.OrdinalIgnoreCase)) blcb = "БЛЦБ-" + blcb;
        return $"({project.ThicknessMm.ToString("0.############################", CultureInfo.GetCultureInfo("ru-RU"))} мм)  {number},  {manager},  ({customer}),  {blcb}";
    }

    private static string Required(string? value, string field)
    {
        var text = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        if (text.Length == 0) throw new WorkflowValidationException($"Укажите и сохраните {field} перед подписанием карт.");
        return text;
    }
}
