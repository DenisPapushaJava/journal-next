using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Tables;

internal sealed class JournalTableSorting : IDisposable
{
    private readonly DataGrid _table;
    private readonly Action<string, string> _showState;
    private readonly Func<ListSortDirection> _defaultDirection;
    private IDataGridCollectionView? _view;
    private KeyModifiers _modifiers;
    private bool _queued;
    private bool _updating;
    private bool _disposed;
    private bool _usingDefault = true;

    internal JournalTableSorting(DataGrid table, Action<string, string> showState, Func<ListSortDirection> defaultDirection)
    {
        _table = table; _showState = showState; _defaultDirection = defaultDirection;
        table.Sorting += OnSorting;
        table.PropertyChanged += OnTableChanged;
        table.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        table.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    internal void Synchronize()
    {
        if (_disposed) return;
        if (!ReferenceEquals(_view, _table.CollectionView))
        {
            if (_view is not null) _view.SortDescriptions.CollectionChanged -= OnSortChanged;
            _view = _table.CollectionView;
            if (_view is not null) _view.SortDescriptions.CollectionChanged += OnSortChanged;
        }
        if (_view is not { CanSort: true }) return;
        // Нативный Ctrl+клик и программный Clear не должны показывать порядок чтения MDB.
        if (_view.SortDescriptions.Count == 0 || _usingDefault && !MatchesDefault()) RestoreDefault();
        else ShowState();
    }

    private bool MatchesDefault() => _view?.SortDescriptions is { Count: 1 } sorts && sorts[0].HasPropertyPath &&
        sorts[0].PropertyPath == nameof(OrderRowViewModel.OrderDateSortKey) && sorts[0].Direction == _defaultDirection();

    internal void RestoreDefault()
    {
        if (_disposed || _table.CollectionView is not { CanSort: true } view) return;
        _usingDefault = true;
        _updating = true;
        try
        {
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(OrderRowViewModel.OrderDateSortKey), _defaultDirection()));
            }
        }
        finally { _updating = false; }
        Synchronize();
    }

    internal void SortColumn(DataGridColumn column, ListSortDirection direction)
    {
        if (_disposed || !column.CanUserSort || !_table.CanUserSortColumns || _table.CollectionView is not { CanSort: true } view) return;
        var path = Path(column);
        if (column.CustomSortComparer is null && string.IsNullOrEmpty(path)) return;
        _usingDefault = false;
        // Column.Sort ставит событие в очередь. Явное направление меню задаём сразу,
        // чтобы оно не попало в цикл третьего клика заголовка и не зависело от Shift/Ctrl.
        _updating = true;
        try
        {
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(column.CustomSortComparer is { } comparer
                    ? DataGridSortDescription.FromComparer(comparer, direction)
                    : DataGridSortDescription.FromPath(path!, direction, view.Culture));
            }
        }
        finally { _updating = false; }
        _modifiers = KeyModifiers.None;
        Synchronize();
    }

    private static string? Path(DataGridColumn column) => column.SortMemberPath is { Length: > 0 } path ? path :
        column is DataGridBoundColumn bound ? bound.Binding switch
        {
            Binding binding => binding.Path,
            ReflectionBinding binding => binding.Path,
            CompiledBinding binding => binding.Path?.ToString(),
            _ => null
        } : null;

    private void OnSorting(object? sender, DataGridColumnEventArgs e)
    {
        var modifiers = _modifiers;
        _modifiers = KeyModifiers.None;
        if (!_usingDefault && !e.Handled && modifiers == KeyModifiers.None && _view?.SortDescriptions.Any(sort =>
            sort.HasPropertyPath && sort.PropertyPath == Path(e.Column) && sort.Direction == ListSortDirection.Descending) == true)
        {
            // Третий обычный клик возвращает стандарт, а не исходную последовательность ItemsSource.
            e.Handled = true;
            RestoreDefault();
        }
        else if (!e.Handled) _usingDefault = false;
        Schedule();
    }

    private void ShowState()
    {
        if (_view is null) return;
        var sorts = _view.SortDescriptions;
        if (_usingDefault)
        { _showState("", ""); return; }
        var labels = sorts.Select(sort =>
        {
            var column = _table.Columns.FirstOrDefault(column => sort.HasPropertyPath
                ? Path(column) == sort.PropertyPath
                : sort is DataGridComparerSortDescription comparer && ReferenceEquals(column.CustomSortComparer, comparer.SourceComparer));
            return $"{column?.Header?.ToString() ?? "Другой столбец"} {(sort.Direction == ListSortDirection.Ascending ? "↑" : "↓")}";
        }).ToArray();
        var summary = labels.Length == 0 ? "" : "Порядок: " + labels[0] + (labels.Length > 1 ? $" +{labels.Length - 1}" : "");
        _showState(summary, string.Join(" → ", labels));
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual visual && (visual is DataGridColumnHeader || visual.FindAncestorOfType<DataGridColumnHeader>() is not null))
            _modifiers = e.KeyModifiers;
    }
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && e.Source is Visual visual &&
            (visual is DataGridColumnHeader || visual.FindAncestorOfType<DataGridColumnHeader>() is not null)) _modifiers = e.KeyModifiers;
    }
    private void OnTableChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property.Name == nameof(DataGrid.ItemsSource)) Schedule(); }
    private void OnSortChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_updating) return;
        // Ручной порядок может совпадать со стандартом. Намерение сохраняется при смене периода.
        _usingDefault = false;
        Schedule();
    }
    private void Schedule()
    {
        if (_queued || _disposed) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; Synchronize(); }, DispatcherPriority.Loaded);
    }
    public void Dispose()
    {
        _disposed = true;
        if (_view is not null) _view.SortDescriptions.CollectionChanged -= OnSortChanged;
        _table.Sorting -= OnSorting;
        _table.PropertyChanged -= OnTableChanged;
        _table.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        _table.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
    }
}
