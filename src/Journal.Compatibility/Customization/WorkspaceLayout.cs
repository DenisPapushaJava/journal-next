using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jornal.Desktop.Customization;

/// <summary>Дерево разделений панелей; прежняя область статусов схлопывается при чтении профиля.</summary>
public sealed record WorkspaceLayout
{
    public const double MaximumHeight = 540;
    public static IReadOnlyList<string> PanelIds { get; } = Array.AsReadOnly(new[] { "result", "orders", "clipboard" });
    public int TreeVersion { get; init; } = 1;
    public WorkspaceNode? Root { get; init; }
    public string[] Panels { get; init; } = ["result", "orders", "clipboard"];
    public double[] ColumnWidths { get; init; } = [2, 1, 1];
    public double TopFraction { get; init; } = .43;
    public double Height { get; init; } = 272;

    /// <summary>Макет пользователя: две компактные панели слева, редактор занимает остаток справа.</summary>
    public static WorkspaceLayout CreateDefault() => new()
    {
        Panels = ["orders", "clipboard", "result"],
        ColumnWidths = [304, 304, 1240],
        Height = ScreenLayout.MinimumResultHeight
    };

    public WorkspaceLayout Normalize(double resultMinimumHeight = ScreenLayout.MinimumResultHeight)
    {
        var defaults = new WorkspaceLayout();
        var panels = (Panels ?? []).Where(defaults.Panels.Contains).Distinct()
            .Concat(defaults.Panels).Distinct().ToArray();
        var widths = Enumerable.Range(0, 3).Select(index => ColumnWidths is { Length: 3 } &&
            double.IsFinite(ColumnWidths[index]) && ColumnWidths[index] is >= .01 and <= 10000
                ? ColumnWidths[index] : defaults.ColumnWidths[index]).ToArray();
        var fraction = double.IsFinite(TopFraction) ? Math.Clamp(TopFraction, .2, .8) : defaults.TopFraction;
        var root = TreeVersion == 1 ? WorkspaceNode.ReadCompatible(Root) : null;
        if (root is null && Panels?.Contains("status") == true)
        {
            var oldIds = new[] { "result", "orders", "status", "clipboard" };
            var oldPanels = Panels.Where(oldIds.Contains).Distinct().Concat(oldIds).Distinct().ToArray();
            root = WorkspaceNode.ReadCompatible(WorkspaceNode.Split(WorkspaceAxis.Columns,
                [WorkspaceNode.Panel(oldPanels[0]), WorkspaceNode.Split(WorkspaceAxis.Rows,
                    [WorkspaceNode.Panel(oldPanels[1]), WorkspaceNode.Panel(oldPanels[2])], fraction, 1 - fraction), WorkspaceNode.Panel(oldPanels[3])], widths));
        }
        root ??= WorkspaceNode.Split(WorkspaceAxis.Columns, panels.Select(WorkspaceNode.Panel).ToArray(), widths);
        return this with
        {
            Root = root, TreeVersion = 1, Panels = root.PanelIds().ToArray(), ColumnWidths = widths, TopFraction = fraction,
            Height = double.IsFinite(Height) ? Math.Clamp(Height, resultMinimumHeight, MaximumHeight) : defaults.Height
        };
    }
}

internal sealed class WorkspaceLayoutConverter : JsonConverter<WorkspaceLayout>
{
    public override WorkspaceLayout? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        try { return document.RootElement.Deserialize<WorkspaceLayout>()?.Normalize(); }
        catch (JsonException) { return new(); }
    }

    public override void Write(Utf8JsonWriter writer, WorkspaceLayout value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Normalize());
}
