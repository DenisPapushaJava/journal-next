using Avalonia;
using Avalonia.Controls;

namespace Jornal.Desktop.Views;

internal static class WindowSizing
{
    public static void FitToWorkingArea(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window.Owner ?? window) ?? window.Screens.Primary;
        if (screen is null) return;

        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        const int margin = 8;
        if (area.Width <= margin * 2 || area.Height <= margin * 2 || scale <= 0) return;
        var frame = window.FrameSize ?? window.ClientSize;
        var borderWidth = Math.Max(0, frame.Width - window.ClientSize.Width);
        var borderHeight = Math.Max(0, frame.Height - window.ClientSize.Height);
        var maxWidth = Math.Max(1, Math.Floor((area.Width - margin * 2) / scale - borderWidth));
        var maxHeight = Math.Max(1, Math.Floor((area.Height - margin * 2) / scale - borderHeight));
        var width = Math.Min(window.Width, maxWidth);
        var height = Math.Min(window.Height, maxHeight);

        // At high display scaling even the usual minimum can exceed the working area.
        window.MinWidth = Math.Min(window.MinWidth, maxWidth);
        window.MinHeight = Math.Min(window.MinHeight, maxHeight);
        window.Width = width;
        window.Height = height;
        var frameWidth = (int)Math.Ceiling((width + borderWidth) * scale);
        var frameHeight = (int)Math.Ceiling((height + borderHeight) * scale);
        window.Position = new PixelPoint(
            Math.Clamp(window.Position.X, area.X + margin, area.Right - margin - frameWidth),
            Math.Clamp(window.Position.Y, area.Y + margin, area.Bottom - margin - frameHeight));
    }
}
