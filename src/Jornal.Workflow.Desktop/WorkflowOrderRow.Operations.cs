using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public sealed partial class WorkflowOrderRow
{
    private const string BendingIcon = "M3,2 L3,12 Q3,16 7,16 L20,16 L20,12 L9,12 Q7,12 7,10 L7,2 Z";
    private const string ChamferIcon = "M2,3 L12,3 L21,12 L21,19 L2,19 Z";
    public IReadOnlyList<WorkflowBadgeItem> OperationBadges { get; private set; } = CreateOperationBadges(order);
    public bool HasOperationBadges => OperationBadges.Count > 0;

    private static WorkflowBadgeItem[] CreateOperationBadges(WorkflowOrder order)
    {
        var pending = order.PendingProjectCount;
        var specifications = order.ActiveSpecifications.ToArray();
        return ForOperation("Фаска", ChamferIcon, p => p.HasChamfer)
            .Concat(ForOperation("Гибка", BendingIcon, p => p.HasBending)).ToArray();

        IEnumerable<WorkflowBadgeItem> ForOperation(string name, string icon, Func<CuttingParameters, bool> marked) =>
            specifications.Where(s => marked(s.Current.Parameters))
            .GroupBy(s => s.Current.Parameters.ThicknessMm).OrderBy(group => group.Key)
            .Select(group => new WorkflowBadgeItem(SpecificationDraft.N(group.Key) + " мм",
                name + " · " + SpecificationDraft.N(group.Key) + " мм · Проектов: " + group.Count() +
                "\nМатериалы: " + string.Join(", ", group.Select(s => s.Current.Parameters.Material.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)) +
                (pending > 0 ? "\nЕщё проектов без спецификации: " + pending : ""), icon));
    }
}
