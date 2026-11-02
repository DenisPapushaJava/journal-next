namespace Jornal.Workflow.Core;

public enum WorkflowWorkFilter { All, NotSent, InWork }

/// <summary>Saved order data only; dates and text search are independent conditions.</summary>
public sealed record WorkflowOrderFilter(Guid? TechnologistId = null, WorkflowWorkFilter Work = WorkflowWorkFilter.All,
    bool WithBending = false, bool WithChamfer = false)
{
    public static WorkflowOrderFilter All { get; } = new();

    public bool Matches(WorkflowOrder order)
    {
        if (TechnologistId is { } id && order.TechnologistId != id) return false;
        if (Work == WorkflowWorkFilter.InWork && !order.HasShopHandoff ||
            Work == WorkflowWorkFilter.NotSent && order.HasShopHandoff) return false;
        if (!WithBending && !WithChamfer) return true;
        var specifications = order.ActiveSpecifications.ToArray();
        return (!WithBending || specifications.Any(s => s.Current.Parameters.HasBending)) &&
            (!WithChamfer || specifications.Any(s => s.Current.Parameters.HasChamfer));
    }
}
