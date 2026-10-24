using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Threading;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowTrashWindowTests
{
    [AvaloniaTheory]
    [InlineData(false, 1366)] [InlineData(true, 1366)]
    [InlineData(false, 1180)] [InlineData(true, 1180)]
    public async Task ConfirmationRestoreAndForeignDeletedDraftFitBothThemesWithoutLosingData(bool dark, int width)
    {
        using var log = new BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var id = before.Orders.Single().Id;
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        preferences.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Dispatcher.UIThread.RunJobs();
            InWindow(window.FindControl<Button>("MoveWorkflowOrderToTrash")!, window);
            InWindow(window.FindControl<Button>("OpenWorkflowTrash")!, window);
            Click(window, "MoveWorkflowOrderToTrash");
            var confirmation = Assert.IsType<WorkflowTrashConfirmationWindow>(Assert.Single(window.OwnedWindows));
            Assert.True(confirmation.FindControl<Button>("CancelWorkflowTrash")!.IsFocused);
            var request = Assert.IsType<WorkflowTrashConfirmationViewModel>(confirmation.DataContext);
            Assert.Contains(before.Orders.Single().ReferenceNumber, request.Title); Assert.Contains("Пример", request.Customer);
            InWindow(confirmation.FindControl<Button>("ConfirmWorkflowTrash")!, confirmation);
            Save(confirmation, dark, width, "workflow-trash-confirmation");
            confirmation.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.OwnedWindows); Assert.Equal(before.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
            Click(window, "MoveWorkflowOrderToTrash");
            confirmation = Assert.IsType<WorkflowTrashConfirmationWindow>(Assert.Single(window.OwnedWindows));
            Click(confirmation, "ConfirmWorkflowTrash"); await Until(() => window.OwnedWindows.Count == 0);
            Assert.Empty(f.Model.Orders); Assert.False(f.Model.IsOrderOpen);
            Click(window, "OpenWorkflowTrash");
            var bin = Assert.IsType<WorkflowTrashWindow>(Assert.Single(window.OwnedWindows));
            if (width == 1180) { bin.Width = 800; bin.Height = 520; }
            var model = Assert.IsType<WorkflowTrashViewModel>(bin.DataContext); await Until(() => !model.IsBusy);
            Dispatcher.UIThread.RunJobs(); Assert.Equal(id, Assert.Single(model.Orders).Order.Id);
            InWindow(bin.FindControl<DataGrid>("WorkflowTrashOrders")!, bin); InWindow(bin.FindControl<Button>("RestoreWorkflowOrder")!, bin);
            Save(bin, dark, width, "workflow-trash");
            Click(bin, "RestoreWorkflowOrder"); await Until(() => !model.IsBusy);
            Assert.Null(model.Error); Assert.Empty(model.Orders); Assert.False(model.RestoreCommand.CanExecute(null));
            Assert.Equal(id, Assert.Single(f.Model.Orders).Order.Id); Assert.True(File.Exists(project.NativePath));
            Click(bin, "CloseWorkflowTrash"); Dispatcher.UIThread.RunJobs();
            Assert.True(f.Model.IsCurrentOrderActive); Assert.False(f.Model.HasCardReview);
            Assert.False(f.Model.ResultIsStale);
            var draft = f.Model.SelectedDraft!; draft.Coefficient = "1,7";
            var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = book.Orders.Single();
            await f.Store.WriteOrderAsync(id, order.Version, w => WorkflowChanges.MoveOrderToTrash(w, id, order.Version,
                order.TechnologistId, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            Click(window, "OpenWorkflowTrash"); bin = Assert.IsType<WorkflowTrashWindow>(Assert.Single(window.OwnedWindows));
            model = Assert.IsType<WorkflowTrashViewModel>(bin.DataContext); await Until(() => !model.IsBusy);
            Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("1,7", draft.Coefficient);
            Assert.False(f.Model.IsCurrentOrderActive);
            Click(bin, "RestoreWorkflowOrder"); await Until(() => !model.IsBusy); Assert.Null(model.Error);
            Click(bin, "CloseWorkflowTrash"); Dispatcher.UIThread.RunJobs();
            Assert.True(f.Model.IsCurrentCardStale); Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("1,7", draft.Coefficient);
            InWindow(window.FindControl<Border>("WorkflowCardReview")!, window);
            Save(window, dark, width, "workflow-trash-draft-review");
            Click(window, "DiscardWorkflowCardChanges"); Dispatcher.UIThread.RunJobs();
            Assert.False(f.Model.HasCardReview); Assert.Equal("1", f.Model.SelectedDraft!.Coefficient);
            Assert.True(log.Errors.Count == 0, string.Join("\n", log.Errors.Distinct().Take(12)));
        }
        finally
        {
            foreach (var child in window.OwnedWindows.ToArray()) child.Close();
            window.Close();
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < limit) { await Task.Delay(10, TestContext.Current.CancellationToken); Dispatcher.UIThread.RunJobs(); }
        Assert.True(condition());
    }
    private static void Click(Window window, string name)
    {
        var control = window.FindControl<Control>(name)!; InWindow(control, window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static void InWindow(Control control, Window window)
    {
        Assert.True(control.IsEffectivelyVisible); var point = control.TranslatePoint(default, window)!.Value;
        Assert.InRange(point.X, 0, window.ClientSize.Width - control.Bounds.Width);
        Assert.InRange(point.Y, 0, window.ClientSize.Height - control.Bounds.Height);
    }
    private static void Save(Window window, bool dark, int width, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); var image = window.CaptureRenderedFrame(); Assert.NotNull(image);
        image!.Save(Path.Combine(output, $"{name}-{width}-{(dark ? "dark" : "light")}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
    private sealed class BindingLog : ILogSink, IDisposable
    {
        private readonly ILogSink? _previous = Logger.Sink;
        public List<string> Errors { get; } = [];
        public BindingLog() => Logger.Sink = this;
        public void Dispose() => Logger.Sink = _previous;
        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Log(level, area, source, messageTemplate, []);
        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] values) => Errors.Add(messageTemplate + " " + string.Join("; ", values));
    }
}
