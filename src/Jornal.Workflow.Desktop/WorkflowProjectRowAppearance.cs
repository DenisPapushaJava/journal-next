using Avalonia;
using Avalonia.Controls;

namespace Jornal.Workflow.Desktop;

// The binding follows DataContext changes when DataGrid recycles a row.
public sealed class WorkflowProjectRowAppearance : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsExcludedProperty =
        AvaloniaProperty.RegisterAttached<WorkflowProjectRowAppearance, DataGridRow, bool>("IsExcluded");

    static WorkflowProjectRowAppearance() => IsExcludedProperty.Changed.AddClassHandler<DataGridRow>((row, change) =>
        row.Classes.Set("workflowProjectExcluded", change.GetNewValue<bool>()));

    public static bool GetIsExcluded(DataGridRow row) => row.GetValue(IsExcludedProperty);
    public static void SetIsExcluded(DataGridRow row, bool value) => row.SetValue(IsExcludedProperty, value);
}
