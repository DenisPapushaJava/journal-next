using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Jornal.Desktop.Controls;

public sealed class LayoutHandle : Thumb
{
    public static readonly StyledProperty<string> TitleProperty =
        AvaloniaProperty.Register<LayoutHandle, string>(nameof(Title), "");
    public static readonly StyledProperty<string> PanelIdProperty =
        AvaloniaProperty.Register<LayoutHandle, string>(nameof(PanelId), "");
    protected override Type StyleKeyOverride => typeof(LayoutHandle);
    private IPointer? _pointer;
    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string PanelId { get => GetValue(PanelIdProperty); set => SetValue(PanelIdProperty, value); }
    public Point StartPoint { get; private set; }
    public event EventHandler? MoveStarted;
    public event EventHandler<Point>? MovePoint;
    public event EventHandler? MoveFinished;
    public event EventHandler? MoveCanceled;

    public void CancelMove()
    {
        var pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        StartPoint = e.GetPosition(TopLevel.GetTopLevel(this));
        Focus();
        _pointer = e.Pointer;
        e.Pointer.Capture(this);
        base.OnPointerPressed(e);
        MoveStarted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pointer is not null) MovePoint?.Invoke(this, e.GetPosition(TopLevel.GetTopLevel(this)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pointer is null) return;
        MoveFinished?.Invoke(this, EventArgs.Empty);
        CancelMove();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_pointer is null) return;
        _pointer = null;
        MoveCanceled?.Invoke(this, EventArgs.Empty);
    }
}
