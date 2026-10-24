using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Jornal.Desktop.Controls;

namespace Jornal.Desktop.Customization;

public sealed partial class MainWindowCustomization
{
    private static readonly WorkspaceNode CompactWorkspace = WorkspaceLayout.CreateDefault().Normalize().Root!;
    private readonly Dictionary<string, Grid> _workspaceGrids = new();
    private WorkspaceNode? _renderedWorkspace;
    private bool _updatingWorkspace;
    private IPointer? _workspaceResizePointer;
    private WorkspaceNode? _workspaceResizeSnapshot;
    private bool _cancelingWorkspaceResize;
    private WorkspaceDrop? _workspaceDrop;
    private sealed record WorkspaceDrop(WorkspaceNode Root, string Source);
    private Grid Board => Find<Grid>("DockBoard");
    private Control Panel(string id) => id == "result" ? Find<Grid>("ResultSlot") : _panels[id];
    private static string PanelTitle(string id) => id switch
        { "result" => "Результат", "orders" => "Заявки", _ => "Буфер обмена и БЛЦБ" };
    private GridSplitter[] WorkspaceSplitters => [Find<GridSplitter>("WorkspaceFirstSplitter"),
        Find<GridSplitter>("WorkspaceSecondSplitter"), Find<GridSplitter>("WorkspaceRowsSplitter")];

    // Размер окна сам по себе не меняет сохранённые пропорции, в том числе при временной адаптации.
    private WorkspaceLayout? CaptureWorkspace() => _layout.Workspace;

    private void DetachWorkspaceControls()
    {
        foreach (var panel in _panels.Values.Cast<Control>().Append(Find<Grid>("ResultSlot"))
                     .Append(Find<LayoutHandle>("ResultResizeHandle")).Concat(WorkspaceSplitters))
            if (panel.Parent is Panel parent) parent.Children.Remove(panel);
        Board.Children.Clear();
        _workspaceGrids.Clear();
        _renderedWorkspace = null;
    }

    private void ApplyWorkspace()
    {
        DetachWorkspaceControls();
        var docked = _layout.Workspace is not null;
        _groups.IsVisible = !docked;
        Find<Grid>("DockArea").IsVisible = docked;
        Find<Border>("ResultPanel").Margin = docked ? new Thickness(0) : new Thickness(0, 0, 0, 4);
        var resize = Find<LayoutHandle>("ResultResizeHandle");
        ToolTip.SetTip(resize, docked ? "Высота панелей · ↑ ↓" : "Изменить высоту результата · ↑ ↓ с клавиатуры");
        AutomationProperties.SetName(resize, docked ? "Изменить высоту панелей" : "Изменить высоту результата");
        if (!docked)
        {
            foreach (var panel in _panels.Values)
            { Grid.SetRow(panel, 0); Grid.SetRowSpan(panel, 1); Grid.SetColumnSpan(panel, 1); _groups.Children.Add(panel); }
            var result = Find<Grid>("ResultSlot");
            Grid.SetColumn(result, 0); Grid.SetRowSpan(result, 1); Grid.SetColumnSpan(result, 1);
            _top.Children.Add(result); result.Children.Add(resize);
            return;
        }
        Find<Grid>("DockResizeSlot").Children.Add(resize);
        UpdateWorkspaceSize();
    }

    private double AvailableWorkspaceHeight()
    {
        var body = Find<Grid>("BodyGrid");
        if (body.Bounds.Height <= 0) return WorkspaceLayout.MaximumHeight - 8;
        var error = Find<Border>("LoadErrorPanel");
        return Math.Clamp(body.Bounds.Height - 12 - Math.Max(36, Find<Grid>("FiltersPanel").Bounds.Height)
            - 40 - (Model?.IsCustomizing == true ? 96 : 128) - 2 - 8
            - (error.IsVisible ? error.Bounds.Height + error.Margin.Bottom : 0), _layout.ResultMinimumHeight, WorkspaceLayout.MaximumHeight - 8);
    }

    private double AvailableWorkspaceWidth() => Find<Grid>("BodyGrid").Bounds.Width is > 0 and var width
        ? width : Math.Max(0, _window.ClientSize.Width - 48);

    private bool WorkspaceFits(WorkspaceNode root)
    {
        var minimum = WorkspaceGeometry.Minimum(root, _layout.ResultMinimumHeight);
        return minimum.Width <= AvailableWorkspaceWidth() + .5 && minimum.Height <= AvailableWorkspaceHeight() + .5;
    }

    private double WorkspaceHeight(WorkspaceLayout workspace, WorkspaceNode root) =>
        Math.Max(WorkspaceGeometry.Minimum(root, _layout.ResultMinimumHeight).Height, Math.Min(workspace.Height - 8, AvailableWorkspaceHeight())) + 8;

    private void UpdateWorkspaceSize()
    {
        if (_updatingWorkspace || _layout.Workspace is not { Root: { } desired } workspace) return;
        _updatingWorkspace = true;
        try
        {
            var root = WorkspaceFits(desired) ? desired : CompactWorkspace;
            if (!ReferenceEquals(_renderedWorkspace, root))
            {
                // Перестройка только при изменении структуры/режима адаптации, не на каждом resize.
                CancelDrag();
                RenderWorkspace(root);
            }
            _top.RowDefinitions[0].Height = new GridLength(WorkspaceHeight(workspace, root));
            _top.RowDefinitions[1].Height = new GridLength(0);
            _top.RowDefinitions[2].Height = new GridLength(0);
        }
        finally { _updatingWorkspace = false; }
    }

    private void RenderWorkspace(WorkspaceNode root)
    {
        // Экземпляры панелей сохраняются: текст, bindings, команды и NameScope не пересоздаются.
        var resize = Find<LayoutHandle>("ResultResizeHandle");
        DetachWorkspaceControls();
        Find<Grid>("DockResizeSlot").Children.Add(resize);
        var splitters = new Queue<GridSplitter>(WorkspaceSplitters);
        void Build(WorkspaceNode node, Grid grid, string path)
        {
            _workspaceGrids[path] = grid;
            grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
            var columns = node.Axis == WorkspaceAxis.Columns;
            if (columns) grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            else grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            for (var index = 0; index < node.Children.Length; index++)
            {
                if (index > 0)
                {
                    if (columns) grid.ColumnDefinitions.Add(new ColumnDefinition(WorkspaceGeometry.Gap, GridUnitType.Pixel));
                    else grid.RowDefinitions.Add(new RowDefinition(WorkspaceGeometry.Gap, GridUnitType.Pixel));
                }
                var minimum = WorkspaceGeometry.Minimum(node.Children[index], _layout.ResultMinimumHeight);
                if (columns) grid.ColumnDefinitions.Add(new ColumnDefinition(node.Weights[index], GridUnitType.Star) { MinWidth = minimum.Width });
                else grid.RowDefinitions.Add(new RowDefinition(node.Weights[index], GridUnitType.Star) { MinHeight = minimum.Height });
            }
            // Сначала границы родителя — три прежних именованных разделителя остаются доступными.
            for (var index = 1; index < node.Children.Length; index++)
            {
                var splitter = splitters.Dequeue();
                splitter.Width = columns ? WorkspaceGeometry.Gap : double.NaN;
                splitter.Height = columns ? double.NaN : WorkspaceGeometry.Gap;
                splitter.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                splitter.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                splitter.ResizeDirection = columns ? GridResizeDirection.Columns : GridResizeDirection.Rows;
                splitter.Classes.Set("columns", columns); splitter.Classes.Set("rows", !columns);
                Grid.SetRow(splitter, columns ? 0 : index * 2 - 1); Grid.SetColumn(splitter, columns ? index * 2 - 1 : 0);
                Grid.SetRowSpan(splitter, 1); Grid.SetColumnSpan(splitter, 1);
                ToolTip.SetTip(splitter, columns ? "Ширина панелей · ← →" : "Высота панелей · ↑ ↓");
                AutomationProperties.SetName(splitter, (columns ? "Ширина" : "Высота") + ": " +
                    string.Join(", ", node.Children[index - 1].PanelIds().Concat(node.Children[index].PanelIds()).Select(PanelTitle)));
                grid.Children.Add(splitter);
            }
            for (var index = 0; index < node.Children.Length; index++)
            {
                var child = node.Children[index];
                var control = child.PanelId is { } id ? Panel(id) : new Grid();
                Grid.SetColumn(control, columns ? index * 2 : 0); Grid.SetRow(control, columns ? 0 : index * 2);
                Grid.SetColumnSpan(control, 1); Grid.SetRowSpan(control, 1);
                grid.Children.Add(control);
                if (child.PanelId is null) Build(child, (Grid)control, path + "/" + index);
            }
        }
        Build(root, Board, "root");
        _renderedWorkspace = root;
    }

    private WorkspaceNode CaptureRenderedWorkspace()
    {
        WorkspaceNode CaptureNode(WorkspaceNode node, string path)
        {
            if (node.PanelId is not null) return node;
            var grid = _workspaceGrids[path];
            var weights = Enumerable.Range(0, node.Children.Length).Select(index => node.Axis == WorkspaceAxis.Columns
                ? grid.ColumnDefinitions[index * 2].Width.Value : grid.RowDefinitions[index * 2].Height.Value).ToArray();
            return node with { Weights = weights.All(value => value > 0) ? weights : node.Weights,
                Children = node.Children.Select((child, index) => CaptureNode(child, path + "/" + index)).ToArray() };
        }
        return CaptureNode(_renderedWorkspace!, "root");
    }

    private void WorkspaceSplitterChanged()
    {
        if (_cancelingWorkspaceResize || _applying || _updatingWorkspace || _layout.Workspace is not { } workspace || _renderedWorkspace is null) return;
        // Явное движение разделителя фиксирует видимую компоновку; обычное сжатие окна — нет.
        var root = CaptureRenderedWorkspace();
        _layout = _layout with { Workspace = workspace with { Root = root } };
        _renderedWorkspace = root;
        Changed();
    }

    private bool CancelWorkspaceResize()
    {
        if (_workspaceResizePointer is not { } pointer) return false;
        var snapshot = _workspaceResizeSnapshot!;
        _workspaceResizePointer = null; _workspaceResizeSnapshot = null;
        _cancelingWorkspaceResize = true;
        try { pointer.Capture(null); }
        finally { _cancelingWorkspaceResize = false; }
        RenderWorkspace(snapshot);
        return true;
    }

    private void ResizeHeight(double height) => _layout = _layout.Workspace is { } workspace
        ? _layout with { Workspace = workspace with { Height = Math.Clamp(height, _layout.ResultMinimumHeight, WorkspaceLayout.MaximumHeight) } }
        : _layout with { ResultHeight = Math.Clamp(height, _layout.ResultMinimumHeight, ScreenLayout.MaximumResultHeight) };

    private void ApplyWorkspaceMove(WorkspaceNode root, string source)
    {
        var snapshot = Capture();
        Apply(snapshot with { Workspace = snapshot.Workspace! with { Root = root } });
        Find<LayoutHandle>(source switch { "result" => "ResultMoveHandle", "orders" => "OrdersMoveHandle",
            _ => "ClipboardMoveHandle" }).Focus();
    }

    private void MoveWorkspaceByKey(string source, Key key, bool place)
    {
        if (_renderedWorkspace is null) return;
        var origin = BoundsInWindow(Panel(source));
        var target = _renderedWorkspace.PanelIds().Where(id => id != source)
            .Select(id => (Id: id, Bounds: BoundsInWindow(Panel(id))))
            .Where(item => key switch { Key.Left => item.Bounds.Center.X < origin.Center.X - 1,
                Key.Right => item.Bounds.Center.X > origin.Center.X + 1, Key.Up => item.Bounds.Center.Y < origin.Center.Y - 1,
                _ => item.Bounds.Center.Y > origin.Center.Y + 1 })
            .OrderBy(item => Math.Pow(item.Bounds.Center.X - origin.Center.X, 2) + Math.Pow(item.Bounds.Center.Y - origin.Center.Y, 2))
            .Select(item => item.Id).FirstOrDefault();
        var placement = !place ? WorkspacePlacement.Swap : key switch { Key.Left => WorkspacePlacement.Left,
            Key.Right => WorkspacePlacement.Right, Key.Up => WorkspacePlacement.Top, _ => WorkspacePlacement.Bottom };
        if (target is not null && WorkspaceNode.Move(_renderedWorkspace, source, target, placement) is { } root && WorkspaceFits(root))
            ApplyWorkspaceMove(root, source);
    }

    private void FindWorkspaceTarget(LayoutHandle handle, Point point)
    {
        _workspaceDrop = null;
        if (_renderedWorkspace is null) return;
        var board = BoundsInWindow(Board);
        if (!board.Contains(point)) return;
        var placement = WorkspacePlacement.Swap;
        string? target = null;
        Rect targetBounds = board;
        // Узкий край всего блока позволяет поставить панель над/под всеми остальными.
        if (point.Y - board.Y < 6) placement = WorkspacePlacement.Top;
        else if (board.Bottom - point.Y < 6) placement = WorkspacePlacement.Bottom;
        else if (point.X - board.X < 6) placement = WorkspacePlacement.Left;
        else if (board.Right - point.X < 6) placement = WorkspacePlacement.Right;
        else
        {
            target = _renderedWorkspace.PanelIds().FirstOrDefault(id => id != handle.PanelId && BoundsInWindow(Panel(id)).Contains(point));
            if (target is null) return;
            targetBounds = BoundsInWindow(Panel(target));
            var x = (point.X - targetBounds.X) / targetBounds.Width;
            var y = (point.Y - targetBounds.Y) / targetBounds.Height;
            if (Math.Min(x, 1 - x) < Math.Min(y, 1 - y) && Math.Min(x, 1 - x) < .22)
                placement = x < .5 ? WorkspacePlacement.Left : WorkspacePlacement.Right;
            else if (Math.Min(y, 1 - y) < .25) placement = y < .5 ? WorkspacePlacement.Top : WorkspacePlacement.Bottom;
        }
        if (WorkspaceNode.Move(_renderedWorkspace, handle.PanelId, target, placement) is not { } root) return;
        var allowed = WorkspaceFits(root);
        var rejectedBounds = placement switch
        {
            WorkspacePlacement.Left => new Rect(targetBounds.X, targetBounds.Y, targetBounds.Width / 3, targetBounds.Height),
            WorkspacePlacement.Right => new Rect(targetBounds.Right - targetBounds.Width / 3, targetBounds.Y, targetBounds.Width / 3, targetBounds.Height),
            WorkspacePlacement.Top => new Rect(targetBounds.X, targetBounds.Y, targetBounds.Width, targetBounds.Height / 3),
            WorkspacePlacement.Bottom => new Rect(targetBounds.X, targetBounds.Bottom - targetBounds.Height / 3, targetBounds.Width, targetBounds.Height / 3),
            _ => targetBounds
        };
        var bounds = allowed ? WorkspaceGeometry.Arrange(root, new Rect(board.Position,
            new Size(board.Width, WorkspaceHeight(_layout.Workspace!, root) - 8)), _layout.ResultMinimumHeight)[handle.PanelId] : rejectedBounds;
        ShowDropGuide(bounds);
        var guide = Find<Border>("DropGuide");
        guide.Classes.Set("forbidden", !allowed);
        Find<TextBlock>("DropGuideText").Text = !allowed ? "Недостаточно места" : placement switch
            { WorkspacePlacement.Left => "Слева", WorkspacePlacement.Right => "Справа", WorkspacePlacement.Top => "Сверху",
                WorkspacePlacement.Bottom => "Снизу", _ => "Поменять местами" };
        if (!allowed) Find<TextBlock>("DragPreviewText").Text = PanelTitle(handle.PanelId) + " · Недостаточно места";
        if (allowed) _workspaceDrop = new(root, handle.PanelId);
    }

    private void ShowDropGuide(Control panel) => ShowDropGuide(BoundsInWindow(panel));
    private void ShowDropGuide(Rect bounds)
    {
        var guide = Find<Border>("DropGuide");
        guide.Classes.Remove("forbidden");
        guide.Width = bounds.Width; guide.Height = bounds.Height;
        Canvas.SetLeft(guide, bounds.Left); Canvas.SetTop(guide, bounds.Top);
        Find<TextBlock>("DropGuideText").Text = "Поменять местами";
        guide.IsVisible = true;
    }
}
