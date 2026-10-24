using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;

namespace Jornal.Desktop.Tables;

internal sealed record TableMenuTarget(object? Item, DataGridColumn? Column, bool IsHeader);

/// <summary>Контекст таблицы: ПКМ внутри набора сохраняет выбор, вне него выбирает одну строку.</summary>
internal sealed class TableContextMenu : IDisposable
{
    private readonly DataGrid _table;
    private readonly Func<bool> _canOpen;
    private readonly Action<TableMenuTarget, ContextMenu> _populate;
    private readonly Action _selectionChanged;
    internal ContextMenu Menu { get; } = new();
    internal int Version { get; private set; }

    internal TableContextMenu(DataGrid table, Func<bool> canOpen, Action<TableMenuTarget, ContextMenu> populate,
        Action selectionChanged)
    {
        _table = table; _canOpen = canOpen; _populate = populate; _selectionChanged = selectionChanged;
        table.ContextMenu = Menu;
        table.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        table.AddHandler(InputElement.ContextRequestedEvent, OnContextRequested, RoutingStrategies.Tunnel);
        table.AddHandler(InputElement.KeyUpEvent, JournalMenu.OnContextKeyUp, RoutingStrategies.Tunnel);
        table.SelectionChanged += OnSelectionChanged;
        table.Sorting += OnSorting;
    }

    private static T? Ancestor<T>(object? source) where T : Control => source is T target ? target :
        (source as Visual)?.FindAncestorOfType<T>();

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_table).Properties.IsRightButtonPressed || Ancestor<ScrollBar>(e.Source) is not null) return;
        if (_canOpen() && Ancestor<DataGridRow>(e.Source)?.DataContext is { } item) Select(item);
        // Не передаём ПКМ нативному выбору ячейки: он может заменить массовое выделение.
        e.Handled = true;
        if (_canOpen()) _table.Focus();
    }

    private void Select(object item)
    {
        if (!_table.SelectedItems.Contains(item))
        {
            if (_table.SelectionMode == DataGridSelectionMode.Extended) _table.SelectedItems.Clear();
            _table.SelectedItem = item;
        }
        _selectionChanged();
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!_canOpen()) { e.Handled = true; return; }
        var byPointer = e.TryGetPosition(_table, out var point);
        var source = byPointer ? _table.InputHitTest(point) : e.Source;
        if (Ancestor<ScrollBar>(source) is not null) { e.Handled = true; return; }
        var header = Ancestor<DataGridColumnHeader>(source);
        var column = header is null ? null : _table.Columns.FirstOrDefault(candidate => Equals(candidate.Header, header.Content));
        var item = Ancestor<DataGridRow>(source)?.DataContext;
        if (header is null)
        {
            if (!byPointer) item = _table.SelectedItem ?? _table.SelectedItems.Cast<object>().FirstOrDefault();
            if (item is not null) Select(item);
        }
        Close();
        Menu.Items.Clear();
        _populate(new(item, column, header is not null), Menu);
        if (Menu.Items.Count == 0) e.Handled = true;
        else if (!byPointer && item is not null && header is null)
        {
            // Клавиатурное меню размещается у выбранной строки, а не под всей таблицей.
            var row = _table.GetVisualDescendants().OfType<DataGridRow>()
                .FirstOrDefault(row => row.IsEffectivelyVisible && ReferenceEquals(row.DataContext, item));
            if (row is not null) e.Source = row;
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => Close();
    private void OnSorting(object? sender, DataGridColumnEventArgs e) => Close();
    internal void Close() { Version++; Menu.Close(); }

    public void Dispose()
    {
        Close();
        _table.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _table.RemoveHandler(InputElement.ContextRequestedEvent, OnContextRequested);
        _table.RemoveHandler(InputElement.KeyUpEvent, JournalMenu.OnContextKeyUp);
        _table.SelectionChanged -= OnSelectionChanged;
        _table.Sorting -= OnSorting;
        _table.ContextMenu = null;
    }
}
