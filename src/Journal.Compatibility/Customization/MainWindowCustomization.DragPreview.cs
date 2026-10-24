using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Jornal.Desktop.Controls;

namespace Jornal.Desktop.Customization;

public sealed partial class MainWindowCustomization
{
    private RenderTargetBitmap? _dragBitmap;
    private Control? _dragSource;

    private void ShowDragPreview(LayoutHandle handle, Point point)
    {
        var preview = Find<Border>("DragPreview");
        if (!preview.IsVisible)
        {
            var source = Panel(handle.PanelId);
            // Снимок существует только в памяти на время захвата, без диска/логов/технических отчётов.
            if (source.Bounds.Width > 0 && source.Bounds.Height > 0)
            {
                var picture = Find<Image>("DragPreviewImage");
                var scale = Math.Min(1, Math.Min(320 / source.Bounds.Width, 180 / source.Bounds.Height));
                picture.Width = source.Bounds.Width * scale;
                picture.Height = source.Bounds.Height * scale;
                try
                {
                    // Сохраняем всю панель, но выделяем пиксели только для миниатюры.
                    var displayScale = _window.RenderScaling;
                    _dragBitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(picture.Width * displayScale), (int)Math.Ceiling(picture.Height * displayScale)),
                        new Vector(96 * scale * displayScale, 96 * scale * displayScale));
                    _dragBitmap.Render(source);
                    picture.Source = _dragBitmap;
                }
                catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
                {
                    // При недоступном снимке сохраняются название, контур назначения и сам перенос.
                    _dragBitmap?.Dispose();
                    _dragBitmap = null;
                }
            }
            Find<TextBlock>("DragPreviewText").Text = PanelTitle(handle.PanelId);
            _dragSource = source;
            source.Opacity = .35;
            preview.IsVisible = true;
        }
        var width = Find<Image>("DragPreviewImage").Width + 14;
        var height = Find<Image>("DragPreviewImage").Height + 38;
        Canvas.SetLeft(preview, Math.Clamp(point.X + 16, 0, Math.Max(0, _window.Bounds.Width - width)));
        Canvas.SetTop(preview, Math.Clamp(point.Y + 16, 0, Math.Max(0, _window.Bounds.Height - height)));
    }

    private void ClearDragPreview()
    {
        if (_dragSource is { } source) source.Opacity = 1;
        _dragSource = null;
        Find<Image>("DragPreviewImage").Source = null;
        _dragBitmap?.Dispose();
        _dragBitmap = null;
        Find<Border>("ResultPanel").Classes.Remove("drop-target");
    }
}
