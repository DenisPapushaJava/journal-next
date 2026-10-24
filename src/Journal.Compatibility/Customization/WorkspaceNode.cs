namespace Jornal.Desktop.Customization;

public enum WorkspaceAxis { Columns, Rows }
public enum WorkspacePlacement { Swap, Left, Right, Top, Bottom }

/// <summary>Только данные компоновки; контролы и пользовательский ввод сюда не входят.</summary>
public sealed record WorkspaceNode
{
    public string? PanelId { get; init; }
    public WorkspaceAxis Axis { get; init; }
    public WorkspaceNode[] Children { get; init; } = [];
    public double[] Weights { get; init; } = [];

    public static WorkspaceNode Panel(string id) => new() { PanelId = id };
    public static WorkspaceNode Split(WorkspaceAxis axis, WorkspaceNode[] children, params double[] weights) =>
        new() { Axis = axis, Children = children, Weights = weights };

    public IEnumerable<string> PanelIds() => PanelId is { } id ? [id] : Children.SelectMany(child => child.PanelIds());

    public static WorkspaceNode? Validate(WorkspaceNode? root) => Validate(root, WorkspaceLayout.PanelIds);

    internal static WorkspaceNode? ReadCompatible(WorkspaceNode? root) => Validate(root) ??
        (Validate(root, ["result", "orders", "status", "clipboard"]) is { } legacy ? Remove(legacy, "status") : null);

    private static WorkspaceNode? Validate(WorkspaceNode? root, IReadOnlyList<string> expected)
    {
        var panels = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        WorkspaceNode? Visit(WorkspaceNode? node, int depth)
        {
            // Ограничения защищают и JSON, и ошибочную циклическую модель в памяти.
            if (node is null || depth > 3 || ++count > 7) return null;
            if (node.PanelId is { } id)
                return expected.Contains(id) && panels.Add(id) && node.Children is { Length: 0 }
                    ? Panel(id) : null;
            if (!Enum.IsDefined(node.Axis) || node.Children is not { Length: >= 2 and <= 4 }) return null;
            var children = new List<WorkspaceNode>();
            foreach (var child in node.Children)
            {
                if (Visit(child, depth + 1) is not { } valid) return null;
                children.Add(valid);
            }
            var weights = Enumerable.Range(0, children.Count).Select(index => node.Weights?.Length == children.Count &&
                double.IsFinite(node.Weights[index]) && node.Weights[index] is >= .0001 and <= 10000
                    ? node.Weights[index] : 1).ToArray();
            return Split(node.Axis, children.ToArray(), weights);
        }
        var result = Visit(root, 0);
        return panels.SetEquals(expected) ? result : null;
    }

    private static WorkspaceNode? Remove(WorkspaceNode node, string source)
    {
        if (node.PanelId is { } id) return id == source ? null : node;
        var kept = node.Children.Select((child, index) => (Node: Remove(child, source), Weight: node.Weights[index]))
            .Where(item => item.Node is not null).ToArray();
        return kept.Length == 1 ? kept[0].Node : node with
            { Children = kept.Select(item => item.Node!).ToArray(), Weights = kept.Select(item => item.Weight).ToArray() };
    }

    public static WorkspaceNode? Move(WorkspaceNode root, string source, string? target, WorkspacePlacement placement)
    {
        if (Validate(root) is not { } validRoot) return null;
        root = validRoot;
        if (!Enum.IsDefined(placement) || source == target || !root.PanelIds().Contains(source) ||
            target is not null && !root.PanelIds().Contains(target)) return null;
        if (placement == WorkspacePlacement.Swap)
        {
            if (target is null) return null;
            WorkspaceNode Swap(WorkspaceNode node) => node.PanelId is { } id
                ? Panel(id == source ? target : id == target ? source : id)
                : node with { Children = node.Children.Select(Swap).ToArray() };
            return Validate(Swap(root));
        }
        var axis = placement is WorkspacePlacement.Left or WorkspacePlacement.Right ? WorkspaceAxis.Columns : WorkspaceAxis.Rows;
        var before = placement is WorkspacePlacement.Left or WorkspacePlacement.Top;
        WorkspaceNode Attach(WorkspaceNode node) => Split(axis,
            before ? [Panel(source), node] : [node, Panel(source)], 1, 1);
        WorkspaceNode Insert(WorkspaceNode node) => node.PanelId == target ? Attach(node)
            : node with { Children = node.Children.Select(Insert).ToArray() };
        var remaining = Remove(root, source)!;
        return Validate(target is null ? Attach(remaining) : Insert(remaining));
    }
}
