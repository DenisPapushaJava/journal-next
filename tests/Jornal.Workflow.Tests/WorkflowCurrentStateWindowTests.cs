using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowCurrentStateWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task ProjectPanelOpensTheCurrentSourceInBothThemes(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Update(p => p with { DarkTheme = dark });
        var window = new ReportWindow(preferences) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var tabs = window.FindControl<TabControl>("WorkflowOrderTabs")!;
            Assert.Equal(new[] { "Проекты", "Результат" }, tabs.Items.OfType<TabItem>().Select(t => t.Header));
            f.Model.OrderTabIndex = 0; Pump();
            var open = window.FindControl<Button>("OpenWorkflowSpecification")!; Assert.True(open.IsEffectivelyEnabled); InWindow(open, window);
            Assert.Equal("Сохранить", window.FindControl<JournalButton>("SaveWorkflowSpecification")!.Label);
            var report = f.Model.SelectedDraft!.Saved!.Report!;
            var source = ProjectFixture.ReportPath(project);
            Click(open); await Until(() => window.OpenedReports.Count == 1); Assert.Equal(source, window.OpenedReports[0]);
            f.Model.AddLegacySpecificationDraft(); Pump(); Assert.False(open.IsEffectivelyEnabled);
            f.Model.CancelDraftCommand.Execute(null); Pump(); Assert.True(open.IsEffectivelyEnabled);
            f.Model.ResultText = "Ручное письмо"; f.Model.IsResultExpanded = true; Pump(); Assert.Equal(1, f.Model.OrderTabIndex);
            f.Model.IsResultExpanded = false; Assert.Equal(0, f.Model.OrderTabIndex);
            f.Model.OrderTabIndex = 0; Pump();
            Save("ready");
            File.Delete(report.ArchivePath);
            await ProjectFixture.WriteAsync(project, 20000);
            Click(open); await Until(() => window.OpenedReports.Count == 2);
            Assert.Equal(source, window.OpenedReports[1]);
            Assert.Null(f.Model.Error);

            File.Delete(source);
            Click(open); await Until(() => f.Model.Error?.Contains("не найдена", StringComparison.Ordinal) == true);
            Assert.Equal(2, window.OpenedReports.Count);
            Save("missing"); Assert.Empty(log.Errors);
        }
        finally { WorkflowTestWindows.Close(window); }

        void Save(string state)
        {
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
            Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame();
            frame?.Save(Path.Combine(output, $"current-state-{state}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    [Fact]
    public async Task LongWindowsPdfPathOpensFromVerifiedLocalCopy()
    {
        using var f = new ProjectFixture();
        var token = TestContext.Current.CancellationToken;
        var folder = Path.Combine(f.DirectoryPath, new string('a', 100), new string('b', 120));
        Directory.CreateDirectory(folder);
        var archive = Path.Combine(folder, "Спецификация.pdf");
        byte[] bytes = "%PDF-1.7\n"u8.ToArray();
        await File.WriteAllBytesAsync(archive, bytes, token);
        var report = new AcceptedReport(Guid.NewGuid(), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            bytes.Length, Path.GetFileName(archive), archive, new("", "", "", "", "", "", "", "", "", "", ""));
        Assert.True(archive.Length >= 260);

        var preview = await WorkflowReportFiles.PrepareArchiveForOpeningAsync(report, true, f.DirectoryPath, token);
        Assert.True(preview.Length < 260);
        Assert.EndsWith(".pdf", preview);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(preview, token));
        Assert.Equal(archive, await WorkflowReportFiles.PrepareArchiveForOpeningAsync(report, false, f.DirectoryPath, token));

        await File.AppendAllTextAsync(archive, "changed", token);
        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            WorkflowReportFiles.PrepareArchiveForOpeningAsync(report, true, f.DirectoryPath, token));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(preview)!));
    }
    private sealed class ReportWindow(WorkflowPreferencesStore preferences) : WorkflowWindow(preferences)
    {
        public List<string> OpenedReports { get; } = [];
        protected override Task<bool> LaunchSpecificationAsync(string path) { OpenedReports.Add(path); return Task.FromResult(true); }
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
