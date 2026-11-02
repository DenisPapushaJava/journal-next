using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowShopStatusTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessIsTransientWithoutLayoutJumpsButClipboardErrorsRemainAndOldTimersCannotHideNewAttempt(bool dark)
    {
        using var f = new ProjectFixture(); await WorkflowShopAutomationWindowTests.ReadyHans(f);
        var clock = new MapNoticeClock();
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json")), shopStatusClock: clock)
        { DataContext = f.Model, Width = 1180, Height = 768, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var geometry = Geometry(window);
            var fail = false;
            var preview = await f.Model.PrepareShopTransferAsync(_ => fail ? throw new IOException("clipboard") : Task.CompletedTask);
            Assert.NotNull(preview); Assert.True(preview.TextCopied, preview.Error); Pump();
            Assert.Equal(geometry, Geometry(window)); Assert.True(preview.ShowNotice);
            var timer = clock.Timers.Last(); clock.Advance(4); Pump(); Assert.True(preview.ShowNotice);
            await preview.RefreshAsync(); await preview.CopyAsync(); Assert.True(preview.TextCopied, preview.Error);
            timer.Fire(); Pump(); Assert.True(preview.ShowNotice);
            clock.Advance(5); Pump(); Assert.False(preview.ShowNotice); Assert.Equal(geometry, Geometry(window));

            fail = true;
            await f.Model.RetryShopTransferTextAsync();
            Assert.Same(preview, f.Model.ShopTransfer); Assert.True(preview.AllPrepared, preview.Error);
            Assert.True(preview.NeedsClipboardRetry, preview.Error);
            clock.Advance(60); Pump(); Assert.True(preview.ShowNotice); Assert.NotNull(preview.Error);
            fail = false; await f.Model.RetryShopTransferTextAsync(); Assert.True(preview.TextCopied, preview.Error);
            Pump(); var pending = clock.Timers.Last();
            WorkflowTestWindows.Close(window); Assert.True(pending.Disposed);
            pending.Fire(); Pump(); Assert.False(window.IsVisible);
        }
        finally { WorkflowTestWindows.Close(window); }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static Rect[] Geometry(WorkflowWindow window) => new[] { "WorkflowProjectsGrid", "WorkflowProjectWorkspace", "WorkflowOrderTotal" }
        .Select(name => window.FindControl<Control>(name)!).Select(c => new Rect(c.TranslatePoint(default, window)!.Value, c.Bounds.Size)).ToArray();
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, TestContext.Current.CancellationToken); Pump(); } Pump(); Assert.True(done()); }
}
