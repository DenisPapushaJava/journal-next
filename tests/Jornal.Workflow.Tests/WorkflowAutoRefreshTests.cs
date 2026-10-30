using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowAutoRefreshTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [AvaloniaFact]
    public async Task UnchangedRevisionOnlyReadsMetadataAndExternalRowsUpdateInPlace()
    {
        using var f = await Fixture.Create(); var row = Assert.Single(f.Model.Orders);
        var before = f.Memory.Requests.Count;
        Assert.Equal(WorkflowRefreshOutcome.Unchanged, await f.Model.RefreshAutomaticallyAsync(false, token: Token));
        Assert.Equal([WorkflowAccessOperation.ReadRevision], f.Memory.Requests.Skip(before).Select(r => r.Operation));
        f.Change(o => o with { Blcb = "009337", Version = o.Version + 1 });
        Assert.Equal(WorkflowRefreshOutcome.Refreshed, await f.Model.RefreshAutomaticallyAsync(false, token: Token));
        Assert.Same(row, Assert.Single(f.Model.Orders)); Assert.Equal("009337", f.Model.Blcb); Assert.False(f.Model.HasPendingEdits);
        Assert.False(f.Model.IsBusy); Assert.True(f.Model.CanUse);
    }
    [AvaloniaFact]
    public async Task DirtyCalculationSelectionAndLetterSurviveOwnOrderChangesAndDeletion()
    {
        using var f = await Fixture.Create(ready: true); var draft = f.Model.SelectedDraft!; var project = f.Model.SelectedProject!;
        draft.Length = "35"; f.Model.Blcb = "мой ввод"; f.Model.ResultText = "Ручное письмо";
        f.Change(o => o with { Blcb = "009337", Version = o.Version + 1 });
        await f.Model.RefreshAutomaticallyAsync(false, token: Token);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Same(project, f.Model.SelectedProject); Assert.Equal("35", draft.Length);
        Assert.Equal("мой ввод", f.Model.Blcb); Assert.Equal("Ручное письмо", f.Model.ResultText);
        Assert.True(f.Model.IsCurrentCardStale); Assert.True(f.Model.HasCardReview); Assert.False(f.Model.SaveSpecificationCommand.CanExecute(null)); Assert.Null(f.Model.Error);
        f.Change(o => o with { DeletedAt = DateTimeOffset.UtcNow, DeletedBy = o.TechnologistId, Version = o.Version + 1 });
        await f.Model.RefreshAutomaticallyAsync(false, token: Token);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("Ручное письмо", f.Model.ResultText); Assert.False(f.Model.IsCurrentOrderActive);
        Assert.True(f.Model.HasCardReview); Assert.Empty(f.Model.Orders);
    }
    [AvaloniaFact]
    public async Task CleanChangedCardKeepsProjectAndMarksManualLetterStale()
    {
        using var f = await Fixture.Create(ready: true); var id = f.Model.SelectedProject!.Project.Id;
        f.Model.ResultText = "Моё письмо";
        f.Change(o => o with { Blcb = "003333", Version = o.Version + 1 });
        await f.Model.RefreshAutomaticallyAsync(false, token: Token);
        Assert.Equal(id, f.Model.SelectedProject!.Project.Id); Assert.Equal("003333", f.Model.Blcb);
        Assert.False(f.Model.HasPendingEdits); Assert.Equal("Моё письмо", f.Model.ResultText); Assert.True(f.Model.ResultIsStale);
    }
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedReadCannotOverrideAFilterChangeOrClosedModel(bool close)
    {
        using var f = await Fixture.Create(); f.Change(o => o with { Blcb = "003333", Version = o.Version + 1 });
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transport.Delay = async r => { if (r.Operation == WorkflowAccessOperation.Read) { arrived.TrySetResult(); await release.Task; } };
        var refresh = f.Model.RefreshAutomaticallyAsync(false, token: Token); await arrived.Task.WaitAsync(Token);
        if (close) f.Model.Detach(); else { f.Model.Search = "несуществующий"; }
        release.TrySetResult(); Assert.Equal(WorkflowRefreshOutcome.Deferred, await refresh);
        Assert.NotEqual("003333", f.Model.Blcb); Assert.Null(f.Model.BackgroundRefreshError);
        if (!close) await f.Model.SearchDatabaseNowAsync();
    }
    [AvaloniaFact]
    public async Task InputTypedDuringReadIsPreservedAndUnsafeUiDoesNotAcceptSnapshot()
    {
        using var f = await Fixture.Create(); f.Change(o => o with { Blcb = "003333", Version = o.Version + 1 });
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transport.Delay = async r => { if (r.Operation == WorkflowAccessOperation.Read) { arrived.TrySetResult(); await release.Task; } };
        var safe = true; var refresh = f.Model.RefreshAutomaticallyAsync(false, () => safe, Token); await arrived.Task.WaitAsync(Token);
        f.Model.Blcb = "набираю"; safe = false; release.TrySetResult();
        Assert.Equal(WorkflowRefreshOutcome.Deferred, await refresh); Assert.Equal("набираю", f.Model.Blcb);
        Assert.False(f.Model.IsCurrentCardStale); // Neither store nor UI accepted the detached read.
        f.Transport.Delay = null;
        Assert.Equal(WorkflowRefreshOutcome.Refreshed, await f.Model.RefreshAutomaticallyAsync(false, () => true, Token));
        Assert.Equal("набираю", f.Model.Blcb); Assert.True(f.Model.IsCurrentCardStale);
    }
    [AvaloniaFact]
    public async Task OwnPartialWriteCannotHideAnEarlierColleaguesChange()
    {
        using var f = await Fixture.Create(); var a = f.Memory.Book.Orders.Single();
        var b = a with { Id = Guid.NewGuid(), Number = a.Number + 1, ReferenceNumber = a.CreatedAt.AddSeconds(1).ToLocalTime().ToString("yyyy-MM-dd-HHmmss") };
        f.Memory.Book = f.Memory.Book with { Orders = [a, b], Version = f.Memory.Book.Version + 1 };
        await f.Model.ReloadCommand.ExecuteAsync(null);
        f.Memory.Book = f.Memory.Book with { Orders = [a, b with { Blcb = "коллега", Version = b.Version + 1 }], Version = f.Memory.Book.Version + 1 };
        await f.Store.WriteOrderAsync(a.Id, a.Version, w => w with { Orders = [w.Orders.Single() with { Blcb = "своё", Version = a.Version + 1 }] }, Token);
        await f.Model.RefreshAutomaticallyAsync(false, token: Token);
        Assert.Equal("коллега", f.Model.Orders.Single(r => r.Order.Id == b.Id).Order.Blcb);
    }
    [AvaloniaFact]
    public async Task RefreshKeepsLoadedPagesAndChecksChangedFirstPageAfterLoadMore()
    {
        using var f = await Fixture.Create(); var template = f.Memory.Book.Orders.Single(); var at = DateTimeOffset.Now;
        var orders = Enumerable.Range(1, 240).Select(i => template with { Id = Guid.NewGuid(), Number = i, ReferenceNumber = at.AddSeconds(-i).ToLocalTime().ToString("yyyy-MM-dd-HHmmss"), CreatedAt = at.AddSeconds(-i), UpdatedAt = at.AddSeconds(-i) }).ToArray();
        f.Memory.Book = f.Memory.Book with { Orders = orders, Version = f.Memory.Book.Version + 1 };
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Equal(100, f.Model.Orders.Count);
        var first = orders[0]; f.Memory.Book = f.Memory.Book with { Orders = orders.Select(o => o.Id == first.Id ? o with { Blcb = "коллега", Version = o.Version + 1 } : o).ToArray(), Version = f.Memory.Book.Version + 1 };
        await f.Model.LoadMoreOrdersCommand.ExecuteAsync(null); Assert.Equal(200, f.Model.Orders.Count);
        await f.Model.RefreshAutomaticallyAsync(false, token: Token);
        Assert.Equal(200, f.Model.Orders.Count); Assert.True(f.Model.HasMoreOrders);
        Assert.Equal("коллега", f.Model.Orders.Single(r => r.Order.Id == first.Id).Order.Blcb);
        await f.Model.LoadMoreOrdersCommand.ExecuteAsync(null); Assert.Equal(240, f.Model.Orders.Count);
    }
    [AvaloniaFact]
    public async Task OutageKeepsDataAndRecoversWithoutClearingUserErrorOrUnknownWrite()
    {
        using var f = await Fixture.Create(); f.Model.Blcb = "мой ввод"; f.Model.Error = "ошибка ввода"; var row = f.Model.Orders.Single();
        f.Transport.Failure = new IOException("offline");
        Assert.Equal(WorkflowRefreshOutcome.Failed, await f.Model.RefreshAutomaticallyAsync(false, token: Token));
        Assert.Same(row, f.Model.Orders.Single()); Assert.Equal("мой ввод", f.Model.Blcb); Assert.True(f.Model.HasBackgroundRefreshError); Assert.Equal("ошибка ввода", f.Model.Error);
        f.Transport.Failure = null; await f.Model.RefreshAutomaticallyAsync(false, token: Token); Assert.False(f.Model.HasBackgroundRefreshError);
        f.Memory.LoseNextWriteReply = true; var order = f.Memory.Book.Orders.Single();
        await Assert.ThrowsAsync<WorkflowOutcomeUnknownException>(() => f.Store.WriteOrderAsync(order.Id, order.Version,
            w => w with { Orders = [w.Orders.Single() with { Blcb = "неизвестный исход", Version = order.Version + 1 }] }, Token));
        var count = f.Memory.Requests.Count;
        Assert.Equal(WorkflowRefreshOutcome.Deferred, await f.Model.RefreshAutomaticallyAsync(true, token: Token));
        Assert.Equal(count, f.Memory.Requests.Count); Assert.True(f.Store.RequiresRefresh);
    }
    [AvaloniaFact]
    public async Task PreferenceIsLocalDefaultsOnAndSurvivesOtherSettings()
    {
        using var f = await Fixture.Create(); Assert.True(f.Model.AutomaticJournalRefresh);
        f.Model.AutomaticJournalRefresh = false; f.Model.IsDarkTheme = true;
        Assert.False(f.Preferences.Load().AutomaticJournalRefresh); Assert.True(f.Preferences.Load().DarkTheme);
        var count = f.Memory.Requests.Count;
        Assert.Equal(WorkflowRefreshOutcome.Deferred, await f.Model.RefreshAutomaticallyAsync(true, token: Token)); Assert.Equal(count, f.Memory.Requests.Count);
        f.Model.AutomaticJournalRefresh = true; Assert.True(f.Preferences.Load().AutomaticJournalRefresh);
    }
    [AvaloniaFact]
    public async Task AWriteCancelsPendingBackgroundBeforeItsResultCanApply()
    {
        using var f = await Fixture.Create(); f.Change(o => o with { Blcb = "коллега", Version = o.Version + 1 });
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        f.Transport.Delay = async r => { if (r.Operation == WorkflowAccessOperation.Read) { arrived.TrySetResult(); await release.Task; } };
        f.Transport.Observe = (r, token) => { if (r.Operation == WorkflowAccessOperation.Read) observed = token; };
        var background = f.Model.RefreshAutomaticallyAsync(false, token: Token); await arrived.Task.WaitAsync(Token);
        f.Model.Blcb = "мой ввод";
        var saving = f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.True(observed.IsCancellationRequested);
        release.SetResult(); await saving; Assert.Equal(WorkflowRefreshOutcome.Deferred, await background);
        Assert.Equal("мой ввод", f.Model.Blcb); Assert.True(f.Model.HasPendingEdits); // Stale write is rejected, never replayed.
        Assert.Equal("коллега", f.Memory.Book.Orders.Single().Blcb);
    }
    [AvaloniaFact]
    public async Task SourceReplacementRejectsTheOldSourcesLateSnapshot()
    {
        using var f = await Fixture.Create(); f.Model.Detach(); var next = new MemoryMdb();
        f.Model = new(f.Store, new AppUpdatesViewModel(new WorkflowAppUpdater()), preferences: f.Preferences, databases: next);
        await f.Model.ReloadCommand.ExecuteAsync(null);
        f.Change(o => o with { Blcb = "старая база", Version = o.Version + 1 });
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transport.Delay = async r => { if (r.Operation == WorkflowAccessOperation.Read) { arrived.SetResult(); await release.Task; } };
        var background = f.Model.RefreshAutomaticallyAsync(false, token: Token); await arrived.Task.WaitAsync(Token);
        Assert.Null(await f.Model.ConnectDatabaseAsync(Path.Combine(f.Files.DirectoryPath, "another.mdb"), false, Token));
        release.SetResult(); Assert.Equal(WorkflowRefreshOutcome.Deferred, await background);
        Assert.Equal(next.Id, f.Model.DatabaseId); Assert.Empty(f.Model.Orders); Assert.Null(f.Model.BackgroundRefreshError);
    }
    [AvaloniaFact]
    public async Task PurgedDirtyCardSurvivesRemovalOfItsUnusedCatalogEntries()
    {
        using var f = await Fixture.Create(ready: true); var draft = f.Model.SelectedDraft!; draft.Length = "35";
        f.Model.ResultText = "мой текст";
        f.Memory.Book = f.Memory.Book with { Version = f.Memory.Book.Version + 1, Orders = [], Customers = [], Rates = [], Machines = [] };
        f.Memory.BumpCatalogVersion();
        Assert.Equal(WorkflowRefreshOutcome.Refreshed, await f.Model.RefreshAutomaticallyAsync(false, token: Token));
        Assert.True(f.Model.IsCurrentOrderMissing); Assert.True(f.Model.HasCardReview); Assert.Same(draft, f.Model.SelectedDraft);
        Assert.Equal("35", draft.Length); Assert.NotEqual(Guid.Empty, draft.MachineId); Assert.Equal("мой текст", f.Model.ResultText);
        Assert.NotEmpty(f.Model.OrderTitle); Assert.Null(f.Model.BackgroundRefreshError);
    }
    [Fact]
    public async Task OldPreferenceFileDefaultsToEnabledWithoutResettingTheTheme()
    {
        using var f = new ProjectFixture(); var path = Path.Combine(f.DirectoryPath, "legacy-preferences.json");
        await File.WriteAllTextAsync(path, "{\"DarkTheme\":true}", Token);
        var preferences = new WorkflowPreferencesStore(path);
        Assert.True(preferences.Load().AutomaticJournalRefresh); Assert.True(preferences.Load().DarkTheme);
        preferences.Update(p => p with { AutomaticJournalRefresh = false });
        Assert.False(preferences.Load().AutomaticJournalRefresh); Assert.True(preferences.Load().DarkTheme);
    }
    [AvaloniaFact]
    public async Task HandoffChangeWithTheSameOrderVersionUpdatesStripeWithoutRebuildingCalculation()
    {
        using var f = await Fixture.Create(ready: true);
        f.Change(o => o with { Blcb = "009337", Version = o.Version + 1 }); await f.Model.ReloadCommand.ExecuteAsync(null);
        var draft = f.Model.SelectedDraft; var project = f.Model.SelectedProject; var row = f.Model.Orders.Single();
        f.Change(o => o with { ShopHandoff = new(o.Version, o.TechnologistId, DateTimeOffset.UtcNow) });
        await f.Model.RefreshAutomaticallyAsync(false, token: Token);
        Assert.Same(row, f.Model.Orders.Single()); Assert.True(row.HasShopHandoff);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Same(project, f.Model.SelectedProject); Assert.False(f.Model.HasCardReview);
    }
    [AvaloniaFact]
    public async Task FailedPreferenceWriteKeepsItsOldValueAndDoesNotDisappearOnMetadataRead()
    {
        using var f = await Fixture.Create();
        using var lease = new FileStream(Path.Combine(f.Files.DirectoryPath, "mdb-preferences.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        f.Model.AutomaticJournalRefresh = false;
        Assert.True(f.Model.AutomaticJournalRefresh); Assert.True(f.Model.HasAutomaticJournalRefreshError);
        await f.Model.RefreshAutomaticallyAsync(false, token: Token);
        Assert.True(f.Model.HasAutomaticJournalRefreshError); Assert.True(f.Preferences.Load().AutomaticJournalRefresh);
    }
    [AvaloniaFact]
    public async Task ForcedReadCoversAChangedFileEvenWhenTheGlobalRevisionHasNotAdvanced()
    {
        using var f = await Fixture.Create();
        f.Memory.Book = f.Memory.Book with { Orders = f.Memory.Book.Orders.Select(o => o with { Blcb = "из файла", Version = o.Version + 1 }).ToArray() };
        Assert.Equal(WorkflowRefreshOutcome.Unchanged, await f.Model.RefreshAutomaticallyAsync(false, token: Token));
        Assert.Equal(WorkflowRefreshOutcome.Refreshed, await f.Model.RefreshAutomaticallyAsync(true, token: Token));
        Assert.Equal("из файла", f.Model.Blcb);
    }
    [AvaloniaFact]
    public async Task NativeStyleDtosReuseUnchangedRowObjectsAndBindings()
    {
        using var f = await Fixture.Create(); f.Transport.Roundtrip = true;
        var a = f.Memory.Book.Orders.Single(); var b = a with { Id = Guid.NewGuid(), Number = a.Number + 1, ReferenceNumber = a.CreatedAt.AddSeconds(1).ToString("yyyy-MM-dd-HHmmss") };
        f.Memory.Book = f.Memory.Book with { Version = f.Memory.Book.Version + 1, Orders = [a, b] };
        await f.Model.ReloadCommand.ExecuteAsync(null);
        var row = f.Model.Orders.Single(r => r.Order.Id == a.Id); var order = row.Order; var changes = 0;
        row.PropertyChanged += (_, _) => changes++;
        f.Memory.Book = f.Memory.Book with { Version = f.Memory.Book.Version + 1, Orders = [a, b with { Version = b.Version + 1, Blcb = "изменено" }] };
        await f.Model.RefreshAutomaticallyAsync(false, token: Token);
        Assert.Same(row, f.Model.Orders.Single(r => r.Order.Id == a.Id)); Assert.Same(order, row.Order); Assert.Equal(0, changes);
    }
    internal sealed class Transport(MemoryMdb memory) : IWorkflowAccessTransport
    {
        public Func<WorkflowAccessRequest, Task>? Delay;
        public bool Roundtrip;
        public Exception? Failure;
        public Action<WorkflowAccessRequest, CancellationToken>? Observe;
        public async Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken token = default)
        {
            Observe?.Invoke(request, token);
            if (Failure is { } fail) throw fail;
            var response = await memory.SendAsync(request, token);
            if (Delay is { } delay) await delay(request); // Intentionally ignores cancellation to test late completions.
            return Roundtrip ? System.Text.Json.JsonSerializer.Deserialize<WorkflowAccessResponse>(System.Text.Json.JsonSerializer.Serialize(response))! : response;
        }
    }
    internal sealed class Fixture : IDisposable
    {
        public ProjectFixture Files = null!;
        public MemoryMdb Memory = null!;
        public Transport Transport = null!;
        public WorkflowMdbStore Store = null!;
        public WorkflowViewModel Model = null!;
        public WorkflowPreferencesStore Preferences = null!;
        public static async Task<Fixture> Create(bool ready = false)
        {
            var f = new Fixture { Files = new() }; await f.Files.InitializeAsync();
            if (ready) { var project = await f.Files.AddAsync(); await ProjectFixture.WriteAsync(project); await f.Files.AcceptAsync(); }
            f.Memory = new() { Book = await f.Files.Store.ReadAsync(Token) }; f.Transport = new(f.Memory);
            var path = Path.Combine(f.Files.DirectoryPath, "shared.mdb");
            f.Store = new(path, f.Transport, f.Memory.Id);
            f.Preferences = new(Path.Combine(f.Files.DirectoryPath, "mdb-preferences.json"));
            f.Preferences.Save(new() { DatabasePath = path, DatabaseId = f.Memory.Id, EmployeeId = f.Memory.Book.Orders[0].TechnologistId });
            f.Model = new(f.Store, new AppUpdatesViewModel(new WorkflowAppUpdater()), preferences: f.Preferences);
            await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Null(f.Model.Error); return f;
        }
        public void Change(Func<WorkflowOrder, WorkflowOrder> change) => Memory.Book = Memory.Book with
        { Version = Memory.Book.Version + 1, Orders = Memory.Book.Orders.Select(change).ToArray() };
        public void Dispose() { Model.Detach(); Files.Dispose(); }
    }
}
