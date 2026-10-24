using Avalonia;

namespace Jornal.Desktop.Customization;

/// <summary>Одинаковые ограничения и геометрия для размещения и предварительного контура.</summary>
public static class WorkspaceGeometry
{
    public const double Gap = 12;
    public static Size Minimum(WorkspaceNode node, double resultMinimumHeight = ScreenLayout.MinimumResultHeight)
    {
        if (node.PanelId is { } id) return new(id == "result" ? 480 : 304,
            id switch { "result" => resultMinimumHeight, "orders" => 150, _ => 174 });
        var sizes = node.Children.Select(child => Minimum(child, resultMinimumHeight)).ToArray();
        return node.Axis == WorkspaceAxis.Columns
            ? new(sizes.Sum(size => size.Width) + Gap * (sizes.Length - 1), sizes.Max(size => size.Height))
            : new(sizes.Max(size => size.Width), sizes.Sum(size => size.Height) + Gap * (sizes.Length - 1));
    }

    public static Dictionary<string, Rect> Arrange(WorkspaceNode root, Rect bounds, double resultMinimumHeight = ScreenLayout.MinimumResultHeight)
    {
        var result = new Dictionary<string, Rect>();
        void Visit(WorkspaceNode node, Rect area)
        {
            if (node.PanelId is { } id) { result[id] = area; return; }
            var columns = node.Axis == WorkspaceAxis.Columns;
            var minimums = node.Children.Select(child => columns ? Minimum(child, resultMinimumHeight).Width : Minimum(child, resultMinimumHeight).Height).ToArray();
            var sizes = Distribute((columns ? area.Width : area.Height) - Gap * (node.Children.Length - 1), node.Weights, minimums);
            double offset = 0;
            for (var index = 0; index < sizes.Length; index++)
            {
                Visit(node.Children[index], columns ? new Rect(area.X + offset, area.Y, sizes[index], area.Height)
                    : new Rect(area.X, area.Y + offset, area.Width, sizes[index]));
                offset += sizes[index] + Gap;
            }
        }
        Visit(root, bounds);
        return result;
    }

    private static double[] Distribute(double available, double[] weights, double[] minimums)
    {
        var result = new double[weights.Length];
        var pending = Enumerable.Range(0, weights.Length).ToList();
        while (pending.Count > 0)
        {
            var total = pending.Sum(index => weights[index]);
            var limited = pending.Where(index => available * weights[index] / total < minimums[index]).ToArray();
            if (limited.Length == 0)
            {
                foreach (var index in pending) result[index] = available * weights[index] / total;
                break;
            }
            foreach (var index in limited)
            { result[index] = minimums[index]; available -= result[index]; pending.Remove(index); }
        }
        return result;
    }
}
