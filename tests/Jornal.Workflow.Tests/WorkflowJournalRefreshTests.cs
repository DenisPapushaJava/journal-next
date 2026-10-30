using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

internal sealed class JournalClock : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public void Advance(double seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
}
internal sealed class JournalSubscription : IDisposable
{
    public bool Disposed;
    public void Dispose() => Disposed = true;
}

public sealed class WorkflowJournalRefreshTests
{
    [Fact]
    public async Task DebouncesEventsPollsWithoutThemAndOnlyRefreshesOneRequestAtATime()
    {
        var clock = new JournalClock(); Action<bool>? signal = null; var calls = new List<bool>();
        var pending = new TaskCompletionSource<WorkflowRefreshOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new WorkflowJournalRefresh((force, _) => { calls.Add(force); return calls.Count == 1 ? Task.FromResult(WorkflowRefreshOutcome.Refreshed) : pending.Task; }, a => a(), clock,
            (_, changed, _) => { signal = changed; return Task.FromResult<IDisposable>(new JournalSubscription()); });
        coordinator.Configure("test.mdb", true); await coordinator.TickAsync(true, true);
        clock.Advance(15); await coordinator.TickAsync(true, true); Assert.Equal([true], calls);
        clock.Advance(15); var reading = coordinator.TickAsync(true, true); Assert.Equal(2, calls.Count);
        for (var i = 0; i < 20; i++) { signal!(false); clock.Advance(1); await coordinator.TickAsync(true, true); }
        Assert.Equal(2, calls.Count); pending.SetResult(WorkflowRefreshOutcome.Unchanged); await reading;
        clock.Advance(3); await coordinator.TickAsync(true, true); Assert.Equal(3, calls.Count); // Quiet event, no 15s wait after a metadata-only check.
        Assert.False(calls.Last());
    }
    [Fact]
    public async Task HiddenAndUnsafeWindowsDeferAndDoNotAccumulateMissedAttempts()
    {
        var clock = new JournalClock(); var calls = 0;
        using var coordinator = new WorkflowJournalRefresh((_, _) => { calls++; return Task.FromResult(WorkflowRefreshOutcome.Refreshed); }, a => a(), clock,
            (_, _, _) => Task.FromResult<IDisposable>(new JournalSubscription()));
        coordinator.Configure("test.mdb", true); await coordinator.TickAsync(false, true); clock.Advance(600);
        await coordinator.TickAsync(false, true); await coordinator.TickAsync(true, false); Assert.Equal(0, calls);
        await coordinator.TickAsync(true, true); Assert.Equal(1, calls);
        await coordinator.TickAsync(true, true); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task OldSubscriptionIsDisposedAndLateEventsCannotRequestNewSource()
    {
        var clock = new JournalClock(); var subscriptions = new List<(Action<bool> Changed, JournalSubscription Lease)>(); var calls = 0;
        using var coordinator = new WorkflowJournalRefresh((_, _) => { calls++; return Task.FromResult(calls <= 2 ? WorkflowRefreshOutcome.Refreshed : WorkflowRefreshOutcome.Unchanged); }, a => a(), clock,
            (_, changed, _) => { var lease = new JournalSubscription(); subscriptions.Add((changed, lease)); return Task.FromResult<IDisposable>(lease); });
        coordinator.Configure("a.mdb", true); await coordinator.TickAsync(true, true); clock.Advance(15); await coordinator.TickAsync(true, true);
        coordinator.Configure("b.mdb", true); Assert.True(subscriptions[0].Lease.Disposed);
        await coordinator.TickAsync(true, true); clock.Advance(15); await coordinator.TickAsync(true, true); // b snapshot
        clock.Advance(15); await coordinator.TickAsync(true, true); Assert.Equal(3, calls); // b metadata
        clock.Advance(5); subscriptions[0].Changed(true); clock.Advance(3); await coordinator.TickAsync(true, true); Assert.Equal(3, calls);
        coordinator.Configure("b.mdb", false); Assert.True(subscriptions[1].Lease.Disposed);
        clock.Advance(100); await coordinator.TickAsync(true, true); Assert.Equal(3, calls);
    }
    [Fact]
    public async Task WatchFailureKeepsPollingAndReadFailuresBackOffAndRecover()
    {
        var clock = new JournalClock(); var subscriptions = 0; var calls = 0;
        using var coordinator = new WorkflowJournalRefresh((_, _) => Task.FromResult(++calls < 3 ? WorkflowRefreshOutcome.Failed : WorkflowRefreshOutcome.Unchanged), a => a(), clock,
            (_, _, _) => { subscriptions++; throw new IOException("offline watcher"); });
        coordinator.Configure("test.mdb", true); await coordinator.TickAsync(true, true);
        clock.Advance(15); await coordinator.TickAsync(true, true); Assert.Equal(1, calls);
        clock.Advance(15); await coordinator.TickAsync(true, true); Assert.Equal(2, calls);
        clock.Advance(15); await coordinator.TickAsync(true, true); Assert.Equal(2, calls);
        clock.Advance(15); await coordinator.TickAsync(true, true); Assert.Equal(3, calls); Assert.Equal(1, subscriptions);
        clock.Advance(15); await coordinator.TickAsync(true, true); Assert.Equal(4, calls);
    }
    [Fact]
    public async Task DisposeCancelsPendingReadAndDisposesAWatcherWhichArrivesLate()
    {
        var clock = new JournalClock(); var lease = new JournalSubscription(); var source = new TaskCompletionSource<IDisposable>();
        var coordinator = new WorkflowJournalRefresh((_, _) => Task.FromResult(WorkflowRefreshOutcome.Unchanged), a => a(), clock, (_, _, _) => source.Task);
        coordinator.Configure("test.mdb", true); await coordinator.TickAsync(true, true); coordinator.Dispose(); source.SetResult(lease);
        await Task.Yield(); Assert.True(lease.Disposed);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var complete = new TaskCompletionSource<WorkflowRefreshOutcome>(); CancellationToken observed = default;
        var second = new WorkflowJournalRefresh((_, token) => { observed = token; arrived.SetResult(); return complete.Task; }, a => a(), clock,
            (_, _, _) => Task.FromResult<IDisposable>(new JournalSubscription()));
        second.Configure("b.mdb", true); await second.TickAsync(true, true); clock.Advance(15); var read = second.TickAsync(true, true);
        await arrived.Task; second.Dispose(); Assert.True(observed.IsCancellationRequested); complete.SetResult(WorkflowRefreshOutcome.Refreshed); await read;
    }
    [Fact]
    public async Task ActualFileObserverDetectsChangesAndReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "workflow-watch-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "shared.mdb"); await File.WriteAllTextAsync(path, "initial", TestContext.Current.CancellationToken);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            using var watcher = await WorkflowFileWatcher.SubscribeAsync(path, force => { if (force) replaced.TrySetResult(); else changed.TrySetResult(); }, () => { });
            await File.AppendAllTextAsync(path, "change", TestContext.Current.CancellationToken); await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            File.Move(path, path + ".bak"); await File.WriteAllTextAsync(path, "replacement", TestContext.Current.CancellationToken); await replaced.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { Directory.Delete(root, true); }
    }
}
