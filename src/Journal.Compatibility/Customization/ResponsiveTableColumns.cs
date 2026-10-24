using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Jornal.Desktop.Customization;

// Нативные Star-колонки при переполнении могут сжимать даже вручную заданные Pixel.
// Сохраняем намерение отдельно от временного режима прокрутки.
internal sealed class ResponsiveTableColumns : IDisposable
{
    private readonly DataGrid _table;
    private readonly Dictionary<DataGridColumn, DataGridLength> _widths;
    private ScrollBar? _verticalScrollBar;
    private bool _queued;
    private bool _disposed;
    private double _lastAvailable = -1;
    public bool IsAdjusting { get; private set; }

    public ResponsiveTableColumns(DataGrid table)
    {
        _table = table;
        _widths = table.Columns.Skip(1).ToDictionary(column => column, column => column.Width);
        foreach (var column in _widths.Keys) column.PropertyChanged += OnColumnChanged;
        table.TemplateApplied += OnTemplateApplied;
        table.PropertyChanged += OnTablePropertyChanged;
        // Поддерживает и создание после первого применения шаблона. Повторного обхода в layout нет.
        _verticalScrollBar = table.GetVisualDescendants().OfType<ScrollBar>()
            .FirstOrDefault(bar => bar.Name == "PART_VerticalScrollbar");
        table.LayoutUpdated += OnLayoutUpdated;
        table.Sorting += OnSorting;
        Schedule();
    }
    public DataGridLength DesiredWidth(DataGridColumn column) => _widths[column];
    public void SetWidth(DataGridColumn column, DataGridLength width)
    {
        _widths[column] = width;
        IsAdjusting = true;
        try { column.Width = width; }
        finally { IsAdjusting = false; }
        Schedule();
    }
    private void OnColumnChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (IsAdjusting || sender is not DataGridColumn column) return;
        if (e.Property.Name == nameof(DataGridColumn.Width) && e.OldValue is DataGridLength oldWidth &&
            e.NewValue is DataGridLength width && (oldWidth.Value != width.Value || oldWidth.UnitType != width.UnitType))
        {
            _widths[column] = width;
            Schedule();
        }
        else if (e.Property.Name is nameof(DataGridColumn.IsVisible) or nameof(DataGridColumn.MinWidth) or nameof(DataGridColumn.MaxWidth)) Schedule();
    }
    private double AvailableWidth()
    {
        var vertical = _verticalScrollBar;
        return _table.Bounds.Width - (vertical?.IsVisible == true ? vertical.Bounds.Width : 0);
    }
    private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        _verticalScrollBar = e.NameScope.Find<ScrollBar>("PART_VerticalScrollbar");
        Schedule();
    }
    private void OnTablePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TemplatedControl.TemplateProperty) return;
        _verticalScrollBar = null;
        Schedule();
    }
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (Math.Abs(AvailableWidth() - _lastAvailable) > 0.5) Schedule();
    }
    private void OnSorting(object? sender, DataGridColumnEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        // После сортировки DataGrid пересчитывает полосы по прежнему размеру presenter.
        // Повторное измерение учитывает уже занятую вертикальной полосой дорожку.
        if (!_disposed) _table.InvalidateMeasure();
    }, DispatcherPriority.Loaded);
    private void Schedule()
    {
        if (_queued || _disposed) return;
        _queued = true;
        Dispatcher.UIThread.Post(UpdateWidths, DispatcherPriority.Loaded);
    }
    private void UpdateWidths()
    {
        _queued = false;
        if (_disposed || _table.Bounds.Width <= 0) return;
        _lastAvailable = AvailableWidth();
        var minimum = Math.Max(_table.Columns[0].MinWidth, _table.Columns[0].ActualWidth) + _widths.Where(pair => pair.Key.IsVisible)
            .Sum(pair => pair.Value.IsStar ? pair.Key.MinWidth : Math.Clamp(pair.Value.Value, pair.Key.MinWidth, pair.Key.MaxWidth));
        var overflow = minimum > _lastAvailable + 0.5;
        // При точном заполнении Star-колонки иногда дают переполнение порядка 1e-12.
        // Не показываем ложную полосу; настоящее переполнение по-прежнему прокручивается.
        var scroll = overflow ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        if (_table.HorizontalScrollBarVisibility != scroll) _table.HorizontalScrollBarVisibility = scroll;
        IsAdjusting = true;
        try
        {
            // Сначала убираем Star при переполнении, затем восстанавливаем ручные размеры.
            foreach (var (column, logical) in _widths.OrderBy(pair => pair.Value.IsStar ? 0 : 1))
            {
                var width = overflow && logical.IsStar ? new DataGridLength(column.MinWidth) :
                    new DataGridLength(logical.Value, logical.UnitType);
                if (column.Width.Value != width.Value || column.Width.UnitType != width.UnitType ||
                    overflow && column.IsVisible && !logical.IsStar && Math.Abs(column.ActualWidth - Math.Clamp(logical.Value, column.MinWidth, column.MaxWidth)) > 0.5)
                    column.Width = width;
            }
        }
        finally { IsAdjusting = false; }
    }
    public void Dispose()
    {
        _disposed = true;
        _table.TemplateApplied -= OnTemplateApplied;
        _table.PropertyChanged -= OnTablePropertyChanged;
        _table.LayoutUpdated -= OnLayoutUpdated;
        _table.Sorting -= OnSorting;
        foreach (var column in _widths.Keys) column.PropertyChanged -= OnColumnChanged;
        _verticalScrollBar = null;
    }
}
