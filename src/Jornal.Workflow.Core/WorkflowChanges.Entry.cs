namespace Jornal.Workflow.Core;

public static partial class WorkflowChanges
{
    public static WorkflowWorkspace CreateOrderFromNames(WorkflowWorkspace workspace, string managerName, string customerName,
        Guid actorId, DateTimeOffset at, out Guid orderId)
    {
        managerName = managerName?.Trim() ?? ""; customerName = customerName?.Trim() ?? "";
        if (managerName.Length is 0 or > 255 || managerName.Any(char.IsControl))
            throw new WorkflowValidationException("Укажите менеджера — до 255 символов, без переносов строк.");
        if (customerName.Length is 0 or > 255 || customerName.Any(char.IsControl))
            throw new WorkflowValidationException("Укажите заказчика — до 255 символов, без переносов строк.");
        var managers = workspace.Employees.Where(e => e.Name.Equals(managerName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (managers.Length > 1) throw new WorkflowValidationException("Несколько сотрудников с такой фамилией. Уточните имя или инициалы менеджера.");
        if (workspace.Customers.Count(c => c.Name.Equals(customerName, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new WorkflowValidationException("Несколько заказчиков с таким названием. Уточните название заказчика.");
        var manager = managers.SingleOrDefault() ?? new Employee(Guid.NewGuid(), managerName);
        var next = managers.Length == 0 ? workspace with { Employees = [.. workspace.Employees, manager] } : workspace;
        // Resolving names and creating the order are a single workspace change, with the active actor as the initial technologist.
        return CreateOrder(next, customerName, manager.Id, actorId, actorId, at, out orderId);
    }
}
