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

public sealed class WorkflowTrashPurgeWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 960)] [InlineData(true, 960)]
    [InlineData(false, 800)] [InlineData(true, 800)]
    public async Task SingleAndWholeTrashPurgeRequireConfirmationAndFitBothThemes(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        await WorkflowTrashPurgeTests.TrashCurrentAsync(f);
        await WorkflowTrashPurgeTests.NewOrderAsync(f, "Вторая заявка в корзине"); await WorkflowTrashPurgeTests.TrashCurrentAsync(f);
        var active = await WorkflowTrashPurgeTests.NewOrderAsync(f, "Активная заявка"); f.Model.ResultText = "Письмо активной заявки";
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); prefs.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width == 800 ? 1180 : 1366, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Click(window, "OpenWorkflowTrash");
            var bin = Assert.IsType<WorkflowTrashWindow>(Assert.Single(window.OwnedWindows));
            bin.Width = width; bin.Height = width == 800 ? 520 : 620; Pump();
            var model = Assert.IsType<WorkflowTrashViewModel>(bin.DataContext); await Until(() => !model.IsBusy);
            Assert.Equal(2, model.Orders.Count);
            InWindow(bin.FindControl<Button>("ClearWorkflowTrash")!, bin); InWindow(bin.FindControl<Button>("PurgeSelectedWorkflowOrder")!, bin);
            InWindow(bin.FindControl<Button>("RestoreWorkflowOrder")!, bin); InWindow(bin.FindControl<Button>("CloseWorkflowTrash")!, bin);
            Save(bin, dark, width, "trash");
            var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
            Click(bin, "ClearWorkflowTrash");
            var confirmation = Assert.IsType<WorkflowTrashPurgeConfirmationWindow>(Assert.Single(bin.OwnedWindows));
            var request = Assert.IsType<WorkflowTrashPurgeConfirmationViewModel>(confirmation.DataContext);
            Assert.Contains("Заявок: 2", request.Summary); Assert.True(confirmation.FindControl<Button>("CancelWorkflowPurge")!.IsFocused);
            InWindow(confirmation.FindControl<Button>("ConfirmWorkflowPurge")!, confirmation); Save(confirmation, dark, width, "clear-confirmation");
            Click(confirmation, "CancelWorkflowPurge"); Assert.Empty(bin.OwnedWindows);
            Assert.Equal(before.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);

            model.Selected = model.Orders.Single(row => row.Order.Projects.Any(p => p.Id == project.Id));
            Click(bin, "PurgeSelectedWorkflowOrder");
            confirmation = Assert.IsType<WorkflowTrashPurgeConfirmationWindow>(Assert.Single(bin.OwnedWindows));
            request = Assert.IsType<WorkflowTrashPurgeConfirmationViewModel>(confirmation.DataContext);
            Assert.Contains("спецификаций: 1", request.Summary); Assert.Equal("Удалить заявку навсегда?", request.Title);
            Save(confirmation, dark, width, "single-confirmation");
            confirmation.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Assert.Empty(bin.OwnedWindows); Assert.Equal(before.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
            Click(bin, "PurgeSelectedWorkflowOrder"); confirmation = Assert.IsType<WorkflowTrashPurgeConfirmationWindow>(Assert.Single(bin.OwnedWindows));
            Click(confirmation, "ConfirmWorkflowPurge"); await Until(() => !model.IsBusy && bin.OwnedWindows.Count == 0);
            Assert.Single(model.Orders); Assert.True(File.Exists(project.NativePath)); Assert.Null(model.Error);
            Click(bin, "ClearWorkflowTrash"); confirmation = Assert.IsType<WorkflowTrashPurgeConfirmationWindow>(Assert.Single(bin.OwnedWindows));
            Click(confirmation, "ConfirmWorkflowPurge"); await Until(() => !model.IsBusy && bin.OwnedWindows.Count == 0);
            Assert.Empty(model.Orders); Assert.False(bin.FindControl<Button>("ClearWorkflowTrash")!.IsEnabled);
            Assert.False(bin.FindControl<Button>("PurgeSelectedWorkflowOrder")!.IsEnabled); Assert.False(bin.FindControl<Button>("RestoreWorkflowOrder")!.IsEffectivelyEnabled);
            Save(bin, dark, width, "empty"); Click(bin, "CloseWorkflowTrash");
            Assert.Equal(active, Assert.Single((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders).Id);
            Assert.Equal("Письмо активной заявки", f.Model.ResultText); Assert.True(File.Exists(project.NativePath));
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors.Distinct()));
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToArray())
            { foreach (var child in owned.OwnedWindows.ToArray()) child.Close(); owned.Close(); }
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FailedPurgeLeavesConfirmationOpenAndRequiresRefreshWithoutRetrying()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); await WorkflowTrashPurgeTests.TrashCurrentAsync(f);
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Click(window, "OpenWorkflowTrash");
            var bin = Assert.IsType<WorkflowTrashWindow>(Assert.Single(window.OwnedWindows));
            var model = Assert.IsType<WorkflowTrashViewModel>(bin.DataContext); await Until(() => !model.IsBusy);
            Click(bin, "PurgeSelectedWorkflowOrder");
            var confirmation = Assert.IsType<WorkflowTrashPurgeConfirmationWindow>(Assert.Single(bin.OwnedWindows));
            var request = Assert.IsType<WorkflowTrashPurgeConfirmationViewModel>(confirmation.DataContext);
            using (var lease = new FileStream(f.Store.FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                Click(confirmation, "ConfirmWorkflowPurge"); await Until(() => !request.IsBusy);
                Assert.True(confirmation.IsVisible); Assert.NotNull(request.Error); Assert.False(request.WasPurged);
                Assert.Contains("подтвердить удаление", request.Error); Assert.DoesNotContain("параметры", request.Error);
                Assert.False(confirmation.FindControl<Button>("ConfirmWorkflowPurge")!.IsEnabled);
                Assert.True(confirmation.FindControl<Button>("CancelWorkflowPurge")!.IsEnabled);
                Save(confirmation, false, 800, "failed");
            }
            Click(confirmation, "CancelWorkflowPurge"); Assert.True(model.RequiresRefresh);
            Assert.False(bin.FindControl<Button>("PurgeSelectedWorkflowOrder")!.IsEnabled);
            Click(bin, "RefreshWorkflowTrash"); await Until(() => !model.IsBusy);
            Assert.False(model.RequiresRefresh); Assert.True(bin.FindControl<Button>("PurgeSelectedWorkflowOrder")!.IsEnabled); Assert.Single(model.Orders);
            Click(bin, "CloseWorkflowTrash");
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToArray())
            { foreach (var child in owned.OwnedWindows.ToArray()) child.Close(); owned.Close(); }
            window.Close();
        }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); }
        Pump(); Assert.True(done());
    }
    private static void Click(Window window, string name)
    {
        var control = window.FindControl<Control>(name)!; InWindow(control, window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void InWindow(Control control, Window window)
    {
        Assert.True(control.IsEffectivelyVisible); var point = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(point.X, 0, window.ClientSize.Width - control.Bounds.Width);
        Assert.InRange(point.Y, 0, window.ClientSize.Height - control.Bounds.Height);
    }
    private static void Save(Window window, bool dark, int width, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } path) return;
        Directory.CreateDirectory(path); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        frame.Save(Path.Combine(path, $"purge-{name}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
    private sealed class BindingLog : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Errors { get; } = [];
        public BindingLog() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string template) => Errors.Add(template);
        public void Log(LogEventLevel level, string area, object? source, string template, params object?[] values) => Errors.Add(template + " " + string.Join("; ", values));
    }
}
