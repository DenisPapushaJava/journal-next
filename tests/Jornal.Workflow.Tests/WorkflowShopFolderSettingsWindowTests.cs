using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowShopFolderSettingsWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 800)] [InlineData(true, 800)]
    [InlineData(false, 900)] [InlineData(true, 900)]
    public async Task StorageShowsFullPathsAndExplicitLocalSaveWithoutCreatingFolders(bool dark, int width)
    {
        using var log = new WorkflowWindowTests.BindingLog();
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync("Плазма");
        f.Model.Blcb = "005072"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        var settings = f.Model.CreateGeneralSettings(); settings.SelectedSectionIndex = 1;
        var window = new WorkflowSettingsWindow { DataContext = settings, Width = width, Height = width == 800 ? 580 : 660 };
        try
        {
            f.Model.IsDarkTheme = dark; window.Show(); Pump();
            Assert.Equal(dark ? ThemeVariant.Dark : ThemeVariant.Light, window.ActualThemeVariant);
            var root = window.FindControl<TextBox>("WorkflowShopRoot")!;
            var preview = window.FindControl<TextBox>("WorkflowShopFolderPreview")!;
            var save = window.FindControl<Button>("SaveWorkflowSettings")!;
            root.Text = Path.Combine(f.DirectoryPath, "Принтер на складе"); Pump();
            root.BringIntoView(); Pump();
            Assert.True(preview.IsReadOnly); Assert.Equal(settings.ShopFolderPreview, preview.Text);
            Assert.Contains(project.Name, preview.Text); Assert.Contains("БЛЦБ-005072", preview.Text);
            Assert.Equal(project.Name, Path.GetFileName(preview.Text));
            Assert.False(settings.CanRestart); Assert.True(save.IsEffectivelyEnabled);
            preview.BringIntoView(); Pump();
            InWindow(root, window); InWindow(preview, window); InWindow(save, window);
            Assert.InRange(window.FindControl<ScrollViewer>("WorkflowStoragePage")!.Extent.Width, 0, width - 188 - 46);
            Frame(window, $"shop-path-draft-{width}-{dark}");
            Assert.Null(f.Model.ShopRoot); Assert.False(Directory.Exists(settings.ShopRoot));
            await settings.SaveCommand.ExecuteAsync(null); Pump(); Assert.Null(settings.Error); Assert.False(settings.HasChanges);
            Assert.Equal(settings.ShopRoot, f.Model.ShopRoot); Assert.False(Directory.Exists(settings.ShopRoot));
            Assert.True(settings.CanRestart); Assert.Empty(log.Errors);
            Frame(window, $"shop-path-saved-{width}-{dark}");
        }
        finally { window.Close(); }
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void InWindow(Control control, Window window)
    {
        var p = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(p.X, 0, window.ClientSize.Width); Assert.InRange(p.Y, 0, window.ClientSize.Height);
        Assert.InRange(p.X + control.Bounds.Width, 0, window.ClientSize.Width);
        Assert.InRange(p.Y + control.Bounds.Height, 0, window.ClientSize.Height);
    }
    private static void Frame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        frame.Save(Path.Combine(output, name + ".png"), new PngBitmapEncoderOptions());
    }
}
