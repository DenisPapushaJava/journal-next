using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Jornal.Desktop.ViewModels;
using Jornal.Desktop.Controls;

namespace Jornal.Desktop.Tables;

/// <summary>Контекст видимой области фиксируется непосредственно перед применением снимка.</summary>
internal sealed record JournalTableContext(int[] OrderedIds, int? AnchorId, double AnchorTop, double Vertical,
    double Horizontal, bool HadFocus, DataGridColumn? Column)
{
    private static JournalScrollBar? Bar(DataGrid grid, Orientation orientation) =>
        grid.GetVisualDescendants().OfType<JournalScrollBar>().FirstOrDefault(bar => bar.Orientation == orientation);
    private static DataGridRowsPresenter? Presenter(DataGrid grid) => grid.GetVisualDescendants().OfType<DataGridRowsPresenter>().FirstOrDefault();

    internal static JournalTableContext Capture(DataGrid grid)
    {
        grid.UpdateLayout();
        var presenter = Presenter(grid);
        var first = presenter is null ? null : grid.GetVisualDescendants().OfType<DataGridRow>()
            .Where(row => row.IsVisible && row.DataContext is OrderRowViewModel)
            .Select(row => new { Row = row, Y = row.TranslatePoint(default, presenter)?.Y ?? double.MaxValue })
            .Where(item => item.Y < presenter.Bounds.Height && item.Y + item.Row.Bounds.Height > 0)
            .OrderBy(item => item.Y).FirstOrDefault();
        return new(grid.CollectionView.Cast<OrderRowViewModel>().Select(row => row.Id).ToArray(),
            (first?.Row.DataContext as OrderRowViewModel)?.Id, first?.Y ?? 0,
            Bar(grid, Orientation.Vertical)?.Value ?? 0, Bar(grid, Orientation.Horizontal)?.Value ?? 0,
            grid.IsKeyboardFocusWithin && TopLevel.GetTopLevel(grid) is not Window { IsActive: false }, grid.CurrentColumn);
    }

    internal void Restore(DataGrid grid, IReadOnlyList<OrderRowViewModel> selected)
    {
        if (HadFocus)
        {
            grid.Focus();
            if (selected.Count > 0 && Column is { IsVisible: true } && grid.Columns.Contains(Column)) grid.CurrentColumn = Column;
        }
        // Фокус/валюта DataGrid не должны автоматически отметить соседнюю с удалённой строку.
        var ids = selected.Select(row => row.Id).ToHashSet();
        foreach (var row in grid.SelectedItems.Cast<OrderRowViewModel>().Where(row => !ids.Contains(row.Id)).ToArray())
            grid.SelectedItems.Remove(row);
        if (selected.Count == 0) grid.SelectedItem = null;
        grid.UpdateLayout();
        var rows = grid.CollectionView.Cast<OrderRowViewModel>().ToDictionary(row => row.Id);
        var anchor = AnchorId is { } id && rows.TryGetValue(id, out var found) ? found : null;
        if (anchor is null && AnchorId is { } previous)
        {
            var index = Array.IndexOf(OrderedIds, previous);
            var replacement = OrderedIds.Skip(Math.Max(0, index + 1)).Concat(OrderedIds.Take(Math.Max(0, index)).Reverse())
                .FirstOrDefault(rows.ContainsKey, -1);
            anchor = rows.GetValueOrDefault(replacement);
        }
        var vertical = Bar(grid, Orientation.Vertical);
        if (anchor is not null && vertical is not null)
        {
            grid.ScrollIntoView(anchor, null);
            grid.UpdateLayout();
            var presenter = Presenter(grid);
            var row = grid.GetVisualDescendants().OfType<DataGridRow>().FirstOrDefault(row => row.DataContext is OrderRowViewModel value && value.Id == anchor.Id);
            if (presenter is not null && row?.TranslatePoint(default, presenter) is { } point)
                vertical.ScrollTo(vertical.Value + point.Y - AnchorTop);
        }
        else vertical?.ScrollTo(Vertical);
        if (Bar(grid, Orientation.Horizontal) is { } horizontal)
            horizontal.ScrollTo(Horizontal);
        grid.UpdateLayout();
    }
}
