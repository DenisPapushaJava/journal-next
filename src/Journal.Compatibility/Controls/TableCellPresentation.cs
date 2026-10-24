using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Jornal.Desktop.Controls;

// Единое оформление данных журнала; исходные значения и компоновка строк не меняются.
public sealed class TableCellPresentation : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsNumericProperty =
        AvaloniaProperty.RegisterAttached<TableCellPresentation, DataGridColumn, bool>("IsNumeric");
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<TableCellPresentation, Control, bool>("Enabled");
    public static readonly AttachedProperty<string?> HintProperty =
        AvaloniaProperty.RegisterAttached<TableCellPresentation, TextBlock, string?>("Hint");
    private static readonly AttachedProperty<CellState?> StateProperty =
        AvaloniaProperty.RegisterAttached<TableCellPresentation, Control, CellState?>("State");
    private static readonly FontFeatureCollection NumericFeatures = FontFeatureCollection.Parse("tnum");

    static TableCellPresentation()
    {
        HintProperty.Changed.AddClassHandler<TextBlock>((text, _) => text.GetValue(StateProperty)?.Refresh());
        EnabledProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            if (control is TextBlock text)
            {
                control.GetValue(StateProperty)?.Dispose();
                control.SetValue(StateProperty, change.GetNewValue<bool>() ? new CellState(text) : null);
            }
            else if (control is DataGridColumnHeader header)
            {
                header.AttachedToVisualTree -= AlignHeader;
                header.DetachedFromVisualTree -= DetachHeader;
                header.LayoutUpdated -= HeaderLayoutUpdated;
                if (change.GetNewValue<bool>())
                {
                    header.AttachedToVisualTree += AlignHeader;
                    header.DetachedFromVisualTree += DetachHeader;
                    if (TopLevel.GetTopLevel(header) is not null) ConnectHeader(header);
                }
            }
        });
    }

    public static bool GetIsNumeric(DataGridColumn column) => column.GetValue(IsNumericProperty);
    public static void SetIsNumeric(DataGridColumn column, bool value) => column.SetValue(IsNumericProperty, value);
    public static bool GetEnabled(Control control) => control.GetValue(EnabledProperty);
    public static void SetEnabled(Control control, bool value) => control.SetValue(EnabledProperty, value);
    public static string? GetHint(TextBlock text) => text.GetValue(HintProperty);
    public static void SetHint(TextBlock text, string? value) => text.SetValue(HintProperty, value);

    private static void AlignHeader(object? sender, VisualTreeAttachmentEventArgs e) => ConnectHeader((DataGridColumnHeader)sender!);
    private static void ConnectHeader(DataGridColumnHeader header)
    {
        header.LayoutUpdated -= HeaderLayoutUpdated;
        header.LayoutUpdated += HeaderLayoutUpdated;
        SetHeaderAlignment(header);
    }
    private static void DetachHeader(object? sender, VisualTreeAttachmentEventArgs e) =>
        ((DataGridColumnHeader)sender!).LayoutUpdated -= HeaderLayoutUpdated;
    private static void HeaderLayoutUpdated(object? sender, EventArgs e) => SetHeaderAlignment((DataGridColumnHeader)sender!);
    private static void SetHeaderAlignment(DataGridColumnHeader header)
    {
        if (DataGridColumn.GetColumnContainingElement(header) is { } column)
            header.SetCurrentValue(ContentControl.HorizontalContentAlignmentProperty, GetIsNumeric(column) ? HorizontalAlignment.Right : HorizontalAlignment.Left);
    }

    private sealed class CellState : IDisposable
    {
        private readonly TextBlock _text;
        private readonly TextBlock _tipText = new() { MaxWidth = 400, MaxLines = 0, FontSize = 14,
            TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
        private readonly ToolTip _tip;
        private readonly DispatcherTimer _focusDelay;
        private DataGridCell? _cell;
        private DataGridColumn? _column;
        private TopLevel? _escapeRoot;
        private string? _value;
        private bool _keyboardFocus;
        private bool _dismissed;

        public CellState(TextBlock text)
        {
            _text = text;
            SetEnabled(_tipText, false);
            _tip = new ToolTip { Content = _tipText };
            _focusDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _focusDelay.Tick += ShowForKeyboard;
            text.AttachedToVisualTree += Attach;
            text.DetachedFromVisualTree += Detach;
            if (TopLevel.GetTopLevel(text) is not null) Connect();
        }

        private void Attach(object? sender, VisualTreeAttachmentEventArgs e) => Connect();
        private void Detach(object? sender, VisualTreeAttachmentEventArgs e) => Disconnect();
        private void Connect()
        {
            if (_cell is not null) return;
            _cell = _text.FindAncestorOfType<DataGridCell>();
            if (_cell is null) return;
            _cell.GotFocus += GotFocus;
            _cell.LostFocus += LostFocus;
            _cell.KeyDown += KeyDown;
            _cell.PointerExited += PointerExited;
            _cell.PropertyChanged += CellPropertyChanged;
            _text.LayoutUpdated += LayoutUpdated;
            ToolTip.SetShowDelay(_cell, 600);
            ToolTip.SetBetweenShowDelay(_cell, 600);
            Refresh();
        }

        private void LayoutUpdated(object? sender, EventArgs e) => Refresh();
        public void Refresh()
        {
            if (_cell is null) return;
            if (!_text.IsEffectivelyVisible || !_cell.IsEffectivelyVisible || !InViewport()) { Close(); return; }
            var column = DataGridColumn.GetColumnContainingElement(_text);
            if (!ReferenceEquals(column, _column))
            {
                _column = column;
                var numeric = column is not null && GetIsNumeric(column);
                _text.SetCurrentValue(TextBlock.TextAlignmentProperty, numeric ? TextAlignment.Right : TextAlignment.Left);
                _text.SetCurrentValue(TextBlock.FontFeaturesProperty, numeric ? NumericFeatures : null);
            }
            var clipped = _text.TextLayout.TextLines.Any(line => line.HasCollapsed);
            var hint = GetHint(_text);
            var value = clipped && !string.IsNullOrEmpty(_text.Text) ? _text.Text : null;
            if (!string.IsNullOrEmpty(hint)) value = value is null ? hint : value + "\n" + hint;
            if (value != _value)
            {
                _value = value;
                _tipText.Text = value;
                ToolTip.SetTip(_cell, value is null ? null : _tip);
                if (value is null) Close();
            }
            if (_keyboardFocus && !_dismissed && value is not null && !_focusDelay.IsEnabled && !ToolTip.GetIsOpen(_cell))
                _focusDelay.Start();
        }

        private void GotFocus(object? sender, FocusChangedEventArgs e)
        {
            _keyboardFocus = e.NavigationMethod != NavigationMethod.Pointer;
            _dismissed = false;
            Refresh();
        }
        private void LostFocus(object? sender, FocusChangedEventArgs e)
        {
            _keyboardFocus = false;
            _dismissed = false;
            Close();
            if (_cell is not null) ToolTip.SetServiceEnabled(_cell, true);
        }
        private void ShowForKeyboard(object? sender, EventArgs e)
        {
            _focusDelay.Stop();
            if (_cell is { IsKeyboardFocusWithin: true } && _keyboardFocus && !_dismissed && _value is not null)
                ToolTip.SetIsOpen(_cell, true);
        }
        private void KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || _cell is null || !(ToolTip.GetIsOpen(_cell) || _focusDelay.IsEnabled)) return;
            _dismissed = true;
            Close();
            ToolTip.SetServiceEnabled(_cell, false);
            e.Handled = true;
        }
        private void CellPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ToolTip.IsOpenProperty)
                SetEscapeRoot(_cell is not null && ToolTip.GetIsOpen(_cell) ? TopLevel.GetTopLevel(_cell) : null);
        }
        private void SetEscapeRoot(TopLevel? root)
        {
            if (ReferenceEquals(root, _escapeRoot)) return;
            _escapeRoot?.RemoveHandler(InputElement.KeyDownEvent, KeyDown);
            _escapeRoot = root;
            _escapeRoot?.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        }
        private void PointerExited(object? sender, PointerEventArgs e)
        {
            if (_cell is not null) ToolTip.SetServiceEnabled(_cell, true);
        }
        private void Close()
        {
            _focusDelay.Stop();
            if (_cell is not null) ToolTip.SetIsOpen(_cell, false);
        }
        private bool InViewport()
        {
            var cell = _cell;
            var rows = cell?.FindAncestorOfType<DataGridRowsPresenter>();
            return cell is not null && rows is not null && cell.TranslatePoint(default, rows) is { } point &&
                new Rect(point, cell.Bounds.Size).Intersects(new Rect(rows.Bounds.Size));
        }
        private void Disconnect()
        {
            Close();
            SetEscapeRoot(null);
            _text.LayoutUpdated -= LayoutUpdated;
            if (_cell is not null)
            {
                _cell.GotFocus -= GotFocus;
                _cell.LostFocus -= LostFocus;
                _cell.KeyDown -= KeyDown;
                _cell.PointerExited -= PointerExited;
                _cell.PropertyChanged -= CellPropertyChanged;
                ToolTip.SetTip(_cell, null);
                ToolTip.SetServiceEnabled(_cell, true);
            }
            _cell = null; _column = null; _value = null;
            _keyboardFocus = false; _dismissed = false;
        }
        public void Dispose()
        {
            Disconnect();
            _text.AttachedToVisualTree -= Attach;
            _text.DetachedFromVisualTree -= Detach;
            _focusDelay.Tick -= ShowForKeyboard;
        }
    }
}
