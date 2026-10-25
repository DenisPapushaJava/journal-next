using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowSplitterWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task DragHighlightSurvivesPointerLeavingTheHandleAndClearsOnReleaseOrCaptureLoss(bool dark, int width)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); var review = await f.ReviewAsync();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); prefs.Update(p => p with { DarkTheme = dark });
        var main = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width, Height = 768 };
        var report = new WorkflowReportReviewWindow { DataContext = review, Width = width, Height = 768 };
        try
        {
            main.Show(); await Until(() => !f.Model.IsBusy);
            Check(main, "JournalPaneSplitter", true);
            Check(main, "WorkflowCalculationSplitter", false);
            report.RequestedThemeVariant = main.ActualThemeVariant; report.Show(); Pump();
            Check(report, "ReviewPaneSplitter", true);
        }
        finally { report.Close(); main.Close(); }

        void Check(Window window, string name, bool columns)
        {
            var splitter = window.FindControl<GridSplitter>(name)!;
            var away = new Point(5, 5); window.MouseMove(away); Pump();
            var handle = Handle(splitter);
            Assert.True(Math.Min(handle.Bounds.Width, handle.Bounds.Height) <= 4);
            Assert.InRange(Math.Max(handle.Bounds.Width, handle.Bounds.Height), 24, 48);
            var center = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), splitter)!.Value;
            Assert.InRange(Math.Abs(center.X - splitter.Bounds.Width / 2), 0, 1);
            Assert.InRange(Math.Abs(center.Y - splitter.Bounds.Height / 2), 0, 1);
            Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(Assert.IsType<Border>(handle.GetVisualParent()).Background).Color.A);
            var normal = ColorOf(splitter);
            Point Grip() => splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
            window.MouseMove(Grip()); Pump(); Assert.True(splitter.IsPointerOver);
            var active = ColorOf(splitter); Assert.NotEqual(normal, active);
            IPointer? pointer = null;
            void Capture(object? sender, PointerPressedEventArgs e) => pointer = e.Pointer;
            splitter.AddHandler(InputElement.PointerPressedEvent, Capture, RoutingStrategies.Tunnel, handledEventsToo: true);
            try
            {
                var start = Grip(); window.MouseDown(start, MouseButton.Left); Pump();
                foreach (var delta in new[] { 20, 60 })
                {
                    window.MouseMove(start + (columns ? new Vector(delta, 0) : new Vector(0, delta)), RawInputModifiers.LeftMouseButton);
                    Pump(); Assert.Equal(active, ColorOf(splitter));
                }
                window.MouseMove(away, RawInputModifiers.LeftMouseButton); Pump(); Assert.False(splitter.IsPointerOver);
                Assert.Equal(active, ColorOf(splitter)); SaveFrame(window, name);
                window.MouseUp(away, MouseButton.Left); Pump(); Assert.Equal(normal, ColorOf(splitter));

                var wideGrip = splitter.TranslatePoint(columns ? new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 4) :
                    new Point(splitter.Bounds.Width / 4, splitter.Bounds.Height / 2), window)!.Value;
                window.MouseMove(wideGrip); Pump(); Assert.True(splitter.IsPointerOver);
                window.MouseDown(wideGrip, MouseButton.Left); Pump();
                window.MouseMove(away, RawInputModifiers.LeftMouseButton); Pump(); Assert.Equal(active, ColorOf(splitter));
                Assert.NotNull(pointer); pointer.Capture(null); Pump(); Assert.Equal(normal, ColorOf(splitter));
            }
            finally
            {
                window.MouseUp(away, MouseButton.Left); Pump();
                splitter.RemoveHandler(InputElement.PointerPressedEvent, Capture);
            }
        }
        void SaveFrame(Window window, string name)
        {
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
            Directory.CreateDirectory(output); using var image = window.CaptureRenderedFrame();
            image?.Save(Path.Combine(output, $"splitter-drag-{name}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }
    private static Border Handle(GridSplitter splitter) => splitter.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WorkflowSplitterHandle");
    private static Color ColorOf(GridSplitter splitter) => Assert.IsAssignableFrom<ISolidColorBrush>(Handle(splitter).Background).Color;
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
        Pump(); Assert.True(done());
    }
}
