using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowChamferWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task ChamferCanBeSetDuringImportAndEditedInlineAndPricingFitsGeneralSettings(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); var review = await f.ReviewAsync();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); prefs.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var importing = new WorkflowReportReviewWindow { DataContext = review, Width = width, Height = 768, RequestedThemeVariant = window.ActualThemeVariant };
            try
            {
                importing.Show(); Pump(); var chamfer = importing.FindControl<CheckBox>("ReviewHasChamfer")!;
                chamfer.BringIntoView(); Pump(); InWindow(chamfer, importing); Click(chamfer);
                Assert.True(review.Selected!.Draft!.HasChamfer); Assert.True(review.Selected.Ready);
                SaveFrame(importing, "import");
                await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error);
            }
            finally { importing.Close(); }
            Pump(); var inline = window.FindControl<CheckBox>("WorkflowHasChamfer")!;
            inline.BringIntoView(); Pump(); InWindow(inline, window); Assert.True(inline.IsChecked);
            var quote = f.Model.SelectedDraft!.Quote; Click(inline); Assert.False(f.Model.SelectedDraft.HasChamfer);
            Assert.True(f.Model.HasPendingEdits); Assert.Equal(quote, f.Model.SelectedDraft.Quote);
            f.Model.CancelDraftCommand.Execute(null); Pump(); Assert.True(inline.IsChecked);
            SaveFrame(window, "inline");
            using var settings = f.Model.CreateGeneralSettings();
            var options = new WorkflowSettingsWindow { DataContext = settings, Width = 800, Height = 660, RequestedThemeVariant = window.ActualThemeVariant };
            try
            {
                options.Show(); Pump(); var minimum = options.FindControl<TextBox>("WorkflowResultMinimum")!;
                minimum.BringIntoView(); Pump(); InWindow(minimum, options);
                minimum.Focus(); minimum.SelectAll(); options.KeyTextInput("1500"); Pump(); Assert.Null(settings.ResultPriceError);
                Assert.Equal(1500m, f.Model.ResultPricing.MinimumPrice); SaveFrame(options, "settings");
                var use = options.FindControl<CheckBox>("WorkflowResultUseMinimum")!;
                use.BringIntoView(); Pump(); Click(use); Assert.False(f.Model.ResultPricing.UseMinimumPrice); Assert.False(minimum.IsEffectivelyEnabled);
                Click(use); Assert.True(f.Model.ResultPricing.UseMinimumPrice); Assert.True(minimum.IsEffectivelyEnabled);
            }
            finally { options.Close(); }
            f.Model.GenerateResultCommand.Execute(null); Assert.Null(f.Model.Error);
            Assert.Contains("Резка: 1500 ₽.", f.Model.ResultText); Assert.EndsWith("   ФАСКА\n", f.Model.ResultText);
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors));
        }
        finally { window.Close(); }

        void SaveFrame(Window target, string state)
        {
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
            Directory.CreateDirectory(output); using var image = target.CaptureRenderedFrame();
            image?.Save(Path.Combine(output, $"chamfer-{state}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!; var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
        Pump(); Assert.True(done());
    }
    private static void InWindow(Control control, Window window)
    {
        Assert.True(control.IsEffectivelyVisible); var point = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(point.X, 0, window.ClientSize.Width - control.Bounds.Width); Assert.InRange(point.Y, 0, window.ClientSize.Height - control.Bounds.Height);
    }
    private sealed class BindingLog : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink; public List<string> Errors { get; } = [];
        public BindingLog() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string template) => Errors.Add(template);
        public void Log(LogEventLevel level, string area, object? source, string template, params object?[] values) => Errors.Add(template + " " + string.Join("; ", values));
    }
}
