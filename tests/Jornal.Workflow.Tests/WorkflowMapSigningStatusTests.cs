using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

internal sealed class MapNoticeClock : TimeProvider
{
    private double _seconds;
    internal List<NoticeTimer> Timers { get; } = [];
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new NoticeTimer(this, callback, state); timer.Change(dueTime, period); Timers.Add(timer); return timer;
    }
    internal void Advance(double seconds)
    {
        _seconds += seconds;
        foreach (var t in Timers.Where(t => !t.Disposed && t.Due <= _seconds).ToArray()) { t.Due = double.PositiveInfinity; t.Fire(); }
    }
    internal sealed class NoticeTimer(MapNoticeClock owner, TimerCallback callback, object? state) : ITimer
    {
        internal bool Disposed; internal double Due;
        internal void Fire() => callback(state); // Also simulate a callback already queued before disposal.
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = owner._seconds + dueTime.TotalSeconds; return !Disposed; }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

public sealed class WorkflowMapSigningStatusTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task SuccessExpiryIsScopedToTheNoticeAndDetailsDoNotMoveTheWorkspace(bool dark)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        using var maps = new MapSigningFixture(); File.Copy(maps.Create(native: Path.GetFileName(project.NativePath)), Path.Combine(Path.GetDirectoryName(project.NativePath)!, "Карта.pdf"));
        var clock = new MapNoticeClock(); var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var window = new WorkflowWindow(preferences, mapStatusClock: clock) { DataContext = f.Model, Width = 1180, Height = 768, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); var geometry = Geometry(window);
            await f.Model.SignMapsAsync(token: Token); Pump(); Assert.True(f.Model.ShowMapSigningStatus);
            Assert.Equal(geometry, Geometry(window)); var previous = clock.Timers.Last();
            clock.Advance(4); Pump(); Assert.True(f.Model.ShowMapSigningStatus);
            await f.Model.SignMapsAsync(token: Token); Pump(); previous.Fire(); Pump();
            Assert.True(f.Model.ShowMapSigningStatus); Assert.Equal(MapSigningStatus.Unchanged, Assert.Single(f.Model.MapSigningResults).Status);
            var details = window.FindControl<Button>("WorkflowMapSigningDetails")!; details.Flyout!.ShowAt(details); Pump();
            Assert.True(details.Flyout.IsOpen); clock.Advance(20); Pump(); Assert.True(f.Model.ShowMapSigningStatus);
            CompactDetails(window);
            Assert.Equal(geometry, Geometry(window)); Frame(window, "signing-popup-" + dark);
            TopLevel.GetTopLevel(window.FindControl<StackPanel>("WorkflowMapSigningDetailsContent")!)!
                .KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump(); Assert.False(details.Flyout.IsOpen);
            window.FindControl<JournalButton>("PrepareWorkflowProject")!.Focus(); Pump();
            clock.Advance(4); Pump(); Assert.True(f.Model.ShowMapSigningStatus);
            clock.Advance(2); Pump(); Assert.False(f.Model.ShowMapSigningStatus); Assert.True(f.Model.HasMapSigningResults);
            Assert.Equal(geometry, Geometry(window)); Frame(window, "signing-expired-" + dark);
            await f.Model.SignMapsAsync(token: Token); Pump(); Assert.True(f.Model.ShowMapSigningStatus);
            var replaced = clock.Timers.Last(); window.DataContext = null; Pump();
            Assert.True(replaced.Disposed); replaced.Fire(); Pump(); Assert.True(f.Model.ShowMapSigningStatus);
            window.DataContext = f.Model; Pump(); Assert.True(f.Model.ShowMapSigningStatus);
            var last = clock.Timers.Last(); window.Close(); Assert.All(clock.Timers, t => Assert.True(t.Disposed)); last.Fire(); Pump();
        }
        finally { window.Close(); }
    }

    private sealed class PausedWord : IWorkflowWordMapEditor
    {
        internal TaskCompletionSource<WordMapEdit?> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<WordMapEdit?> PrepareAsync(byte[] bytes, string project, string caption, CancellationToken token) => Pending.Task.WaitAsync(token);
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnconfirmedCommitStaysVisibleAndInvalidatesShopPreparation(bool dark)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        using var maps = new MapSigningFixture(); var path = Path.Combine(Path.GetDirectoryName(project.NativePath)!, "Карта.pdf");
        File.Copy(maps.Create(native: Path.GetFileName(project.NativePath)), path);
        var clock = new MapNoticeClock(); var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var window = new WorkflowWindow(preferences, mapStatusClock: clock) { DataContext = f.Model, Width = 1180, Height = 768, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); await f.Model.SignMapsAsync(token: Token); Pump();
            var staleTimer = clock.Timers.Last(); var geometry = Geometry(window); var book = await f.Store.ReadAsync(Token);
            f.Model.ShopTransfer = new(Path.Combine(f.DirectoryPath, "shop"), "test", book.Orders.Single(), book, _ => Task.CompletedTask, _ => Task.CompletedTask);
            var backup = Path.Combine(Path.GetDirectoryName(path)!, ".workflow-map-originals", "revision", "Карта.pdf");
            f.Model.ApplyMapSigningResults([new(project.Id, project.Name, path, MapSigningStatus.NeedsVerification, 1,
                "Карта заменена, но проверка не завершена. Подписание остановлено; проверьте карту и резервную копию вручную.", backup)]);
            Pump(); Assert.Null(f.Model.ShopTransfer); Assert.True(f.Model.HasMapSigningProblems); Assert.True(f.Model.ShowMapSigningStatus);
            Assert.Equal("Карты: требуют внимания 1", f.Model.MapSigningStatusText);
            Assert.Contains("подписано 0", f.Model.MapSigningSummary); Assert.Null(f.Model.Error);
            staleTimer.Fire(); clock.Advance(60); Pump(); Assert.True(f.Model.ShowMapSigningStatus);
            Assert.Equal(geometry, Geometry(window));
            var details = window.FindControl<Button>("WorkflowMapSigningDetails")!; details.Flyout!.ShowAt(details); Pump();
            CompactDetails(window); Assert.Equal(geometry, Geometry(window));
            Assert.Contains(backup, Assert.Single(f.Model.MapSigningResults).Details);
            Frame(window, "signing-needs-verification-" + dark);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task RunningSuccessFailureAndNextActionUseTheSameStatusArea(bool dark)
    {
        var word = new PausedWord();
        using var f = new ProjectFixture(store => new(store, new AppUpdatesViewModel(new WorkflowAppUpdater()), wordMapEditor: word));
        await f.InitializeAsync(); var project = await f.AddAsync("HansLaser");
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single(); var actor = f.Model.CurrentEmployee!.Id;
        var parameters = new CuttingParameters("HansLaser", "Ст3", "Базовая", 6, "Лист", 1, 10, 2, 12, 60, 1, false, null) { MachineId = project.MachineId };
        await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.SaveSpecification(w, order.Id, order.Version,
            project.Id, parameters, "1029-121314", new(2026, 10, 29), actor, DateTimeOffset.UtcNow, profile: project.ImportProfile), Token);
        await f.Model.ReloadCommand.ExecuteAsync(null); f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(project.NativePath)!, "Карта.doc"), "mock", Token);
        var clock = new MapNoticeClock(); var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json"));
        var window = new WorkflowWindow(preferences, mapStatusClock: clock) { DataContext = f.Model, Width = 1180, Height = 768, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); var geometry = Geometry(window);
            var action = f.Model.SignMapsAsync(token: Token); await Until(() => f.Model.IsSigningMaps);
            Assert.Equal("Подписываем карты…", f.Model.MapSigningStatusText); Assert.True(window.FindControl<ProgressBar>("WorkflowBusyProgress")!.IsVisible);
            Assert.Equal(geometry, Geometry(window)); Frame(window, "signing-progress-" + dark);
            word.Pending.SetResult(new(null, 1, 0)); await action; Pump(); Assert.True(f.Model.ShowMapSigningStatus);
            var staleTimer = clock.Timers.Last();
            word.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            action = f.Model.SignMapsAsync(token: Token); await Until(() => f.Model.IsSigningMaps);
            word.Pending.SetException(new IOException("busy")); await action; Pump();
            Assert.True(f.Model.HasMapSigningProblems); Assert.Null(f.Model.Error); Assert.Equal(geometry, Geometry(window));
            staleTimer.Fire(); clock.Advance(60); Pump(); Assert.True(f.Model.ShowMapSigningStatus);
            var details = window.FindControl<Button>("WorkflowMapSigningDetails")!; Assert.False(details.Flyout!.IsOpen);
            details.Flyout.ShowAt(details); Pump(); CompactDetails(window);
            Assert.Equal(geometry, Geometry(window)); Frame(window, "signing-failure-details-" + dark);
            await f.Model.ReloadCommand.ExecuteAsync(null); Pump(); Assert.False(f.Model.ShowMapSigningStatus); Assert.False(details.Flyout.IsOpen);
            Assert.All(clock.Timers, t => Assert.True(t.Disposed));
        }
        finally { if (f.Model.IsBusy) { word.Pending.TrySetCanceled(Token); await Until(() => !f.Model.IsBusy); } window.Close(); }
    }

    private static Rect[] Geometry(Window window) => new[] { "WorkflowProjectsGrid", "WorkflowProjectWorkspace", "WorkflowOrderTotal", "WorkflowStatusBar" }
        .Select(name => window.FindControl<Control>(name)!).Select(c => new Rect(c.TranslatePoint(default, window)!.Value, c.Bounds.Size)).ToArray();
    private static void CompactDetails(Window window)
    {
        var content = window.FindControl<StackPanel>("WorkflowMapSigningDetailsContent")!;
        Assert.All(content.GetVisualDescendants().OfType<SelectableTextBlock>(), text =>
            Assert.True(text.DesiredSize.Width <= content.Bounds.Width, "Map details must wrap within the popup."));
        Assert.All(content.GetVisualAncestors().OfType<ScrollViewer>().Concat(content.GetVisualDescendants().OfType<ScrollViewer>()), scroll =>
        {
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width, "Map details must not require horizontal scrolling.");
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height, "A short message must be fully visible without vertical scrolling.");
        });
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> condition)
    { for (var i = 0; i < 500; i++) { Pump(); if (condition()) return; await Task.Delay(10, Token); } Assert.True(condition()); }
    private static void Frame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WORKFLOW_MAP_SIGNING_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); window.CaptureRenderedFrame()!.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
