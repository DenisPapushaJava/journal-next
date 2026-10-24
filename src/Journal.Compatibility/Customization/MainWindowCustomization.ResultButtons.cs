using Avalonia;
using Avalonia.Controls;
using Jornal.Desktop.Controls;

namespace Jornal.Desktop.Customization;

public sealed partial class MainWindowCustomization
{
    private (string Result, string Copy, string Clear)? _appliedResultButtonColors;
    private void OnResultButtonsChanged(object? sender, ResultButtonAppearance appearance)
    {
        if (_beforeEdit is null) return;
        appearance = appearance.Normalize();
        _layout = _layout with { ResultButtons = appearance,
            ResultHeight = Math.Max(_layout.ResultHeight, appearance.MinimumPanelHeight),
            Workspace = _layout.Workspace is { } workspace ? workspace with { Height = Math.Max(workspace.Height, appearance.MinimumPanelHeight) } : null };
        ApplyResultButtons();
        UpdateWorkspaceMinimums();
        UpdateResultSize();
    }

    private void ApplyResultButtons()
    {
        var appearance = _layout.ResultButtons;
        Find<ResultButtonsEditor>("ResultButtonsCustomization").SetValue(appearance);
        foreach (var name in new[] { "AddResultButton", "CopyResultButton", "ClearResultButton" })
        {
            var button = Find<JournalButton>(name);
            button.Width = button.Height = button.MinHeight = appearance.Size;
            button.CornerRadius = new CornerRadius(appearance.Size / 2d);
            button.IconSize = appearance.IconSize;
        }
        var colors = (appearance.ResultColor, appearance.CopyColor, appearance.ClearColor);
        if (_appliedResultButtonColors == colors) return;
        _appliedResultButtonColors = colors;
        ResultButtonPalette.Create(appearance.ResultColor).Apply(Find<JournalButton>("AddResultButton"));
        ResultButtonPalette.Create(appearance.CopyColor).Apply(Find<JournalButton>("CopyResultButton"));
        ResultButtonPalette.Create(appearance.ClearColor).Apply(Find<JournalButton>("ClearResultButton"));
    }

    private void UpdateWorkspaceMinimums()
    {
        if (_renderedWorkspace is not { } root) return;
        void Visit(WorkspaceNode node, string path)
        {
            if (node.PanelId is not null) return;
            var grid = _workspaceGrids[path];
            for (var index = 0; index < node.Children.Length; index++)
            {
                var minimum = WorkspaceGeometry.Minimum(node.Children[index], _layout.ResultMinimumHeight);
                if (node.Axis == WorkspaceAxis.Columns) grid.ColumnDefinitions[index * 2].MinWidth = minimum.Width;
                else grid.RowDefinitions[index * 2].MinHeight = minimum.Height;
                Visit(node.Children[index], path + "/" + index);
            }
        }
        Visit(root, "root");
    }
}
