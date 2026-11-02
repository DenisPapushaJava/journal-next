using Jornal.Desktop.Customization;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowEntrySuggestions
{
    public string[] Managers { get; init; } = [];
    public string[] Customers { get; init; } = [];
    public string[] Materials { get; init; } = [];
    public WorkflowEntrySuggestions Normalize()
    {
        var value = new EntrySuggestions { Managers = Managers, Customers = Customers, Materials = Materials }.Normalize();
        return new() { Managers = value.Managers, Customers = value.Customers, Materials = value.Materials };
    }
    public static WorkflowEntrySuggestions FromText(string? managers, string? customers, string? materials = null)
    {
        var value = EntrySuggestions.FromText(managers, customers, materials);
        return new() { Managers = value.Managers, Customers = value.Customers, Materials = value.Materials };
    }
    public static bool Same(WorkflowEntrySuggestions? first, WorkflowEntrySuggestions? second) =>
        first is null || second is null ? first is null && second is null :
        first.Managers.SequenceEqual(second.Managers, StringComparer.Ordinal) && first.Customers.SequenceEqual(second.Customers, StringComparer.Ordinal) &&
        first.Materials.SequenceEqual(second.Materials, StringComparer.Ordinal);
}
