using Jornal.Desktop.Customization;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowEntrySuggestions
{
    public string[] Managers { get; init; } = [];
    public string[] Customers { get; init; } = [];
    public WorkflowEntrySuggestions Normalize()
    {
        var value = new EntrySuggestions { Managers = Managers, Customers = Customers }.Normalize();
        return new() { Managers = value.Managers, Customers = value.Customers };
    }
    public static WorkflowEntrySuggestions FromText(string? managers, string? customers)
    {
        var value = EntrySuggestions.FromText(managers, customers);
        return new() { Managers = value.Managers, Customers = value.Customers };
    }
    public static bool Same(WorkflowEntrySuggestions? first, WorkflowEntrySuggestions? second) =>
        first is null || second is null ? first is null && second is null :
        first.Managers.SequenceEqual(second.Managers, StringComparer.Ordinal) && first.Customers.SequenceEqual(second.Customers, StringComparer.Ordinal);
}
