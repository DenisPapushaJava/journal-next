namespace Jornal.Workflow.Core;

public static partial class WorkflowChanges
{
    public static WorkflowWorkspace CreateOrderFromNames(WorkflowWorkspace workspace, string managerName, string customerName,
        Guid actorId, DateTimeOffset at, out Guid orderId)
    {
        var (next, manager) = ResolveManagerName(workspace, managerName);
        customerName = customerName?.Trim() ?? "";
        if (customerName.Length is 0 or > 255 || customerName.Any(char.IsControl))
            throw new WorkflowValidationException("Укажите заказчика — до 255 символов, без переносов строк.");
        if (workspace.Customers.Count(c => c.Name.Equals(customerName, StringComparison.OrdinalIgnoreCase)) > 1)
            throw new WorkflowValidationException("Несколько заказчиков с таким названием. Уточните название заказчика.");
        // Resolving names and creating the order are a single workspace change, with the active actor as the initial technologist.
        return CreateOrder(next, customerName, manager.Id, actorId, actorId, at, out orderId);
    }

    public static WorkflowWorkspace UpdateOrderFromManagerName(WorkflowWorkspace workspace, Guid orderId, long expectedVersion,
        string managerName, Guid technologistId, string? blcb, Guid actorId, DateTimeOffset at)
    {
        RequireActiveOrder(workspace, orderId, expectedVersion);
        var (next, manager) = ResolveManagerName(workspace, managerName);
        return UpdateOrder(next, orderId, expectedVersion, manager.Id, technologistId, blcb, actorId, at);
    }

    private static (WorkflowWorkspace Workspace, Employee Manager) ResolveManagerName(WorkflowWorkspace workspace, string managerName)
    {
        managerName = managerName?.Trim() ?? "";
        if (managerName.Length is 0 or > 255 || managerName.Any(char.IsControl))
            throw new WorkflowValidationException("Укажите менеджера — до 255 символов, без переносов строк.");
        var managers = workspace.Employees.Where(e => e.Name.Equals(managerName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (managers.Length > 1) throw new WorkflowValidationException("Несколько сотрудников с такой фамилией. Уточните имя или инициалы менеджера.");
        var manager = managers.SingleOrDefault() ?? new Employee(Guid.NewGuid(), managerName);
        return (managers.Length == 0 ? workspace with { Employees = [.. workspace.Employees, manager] } : workspace, manager);
    }
}
