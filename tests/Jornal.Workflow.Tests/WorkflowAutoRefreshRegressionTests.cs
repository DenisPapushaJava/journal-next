using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowAutoRefreshRegressionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosedReportDraftRemainsAccessibleButCannotOverwriteAnotherClientsChanges(bool backgroundRefresh)
    {
        using var f = await WorkflowAutoRefreshTests.Fixture.Create(ready: true);
        await ProjectFixture.WriteAsync(f.Memory.Book.Orders.Single().Projects.Single(), length: 20000);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CreateReportReviewAsync(Token));
        var candidate = Assert.Single(review.Candidates); var version = review.PreviewOrderVersion;
        foreach (var conflict in candidate.Conflicts) conflict.Selection = 0;
        candidate.Draft!.Length = "37"; f.Model.ReportReviewClosed(); Assert.True(f.Model.HasPendingEdits);
        Assert.True(review.SaveCommand.CanExecute(null));
        f.Change(o => o with { Blcb = "009337", Version = o.Version + 1 });
        if (backgroundRefresh)
            Assert.Equal(WorkflowRefreshOutcome.Refreshed, await f.Model.RefreshAutomaticallyAsync(false, token: Token));

        Assert.True(f.Model.CanLoadReports);
        var reopened = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CreateReportReviewAsync(Token));
        Assert.Same(review, reopened); Assert.Same(candidate, Assert.Single(reopened.Candidates));
        Assert.Equal("37", candidate.Draft.Length); Assert.Equal(version, reopened.PreviewOrderVersion);
        Assert.Contains("Заявка изменена", reopened.Error); Assert.True(f.Model.HasPendingEdits);
        Assert.Contains("сброшены", reopened.Error);
        Assert.False(reopened.SaveCommand.CanExecute(null)); Assert.False(reopened.RefreshCommand.CanExecute(null));
        var requests = f.Memory.Requests.Count;
        await reopened.SaveCommand.ExecuteAsync(null); await reopened.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(requests, f.Memory.Requests.Count); Assert.Equal("37", candidate.Draft.Length);
        Assert.Equal("009337", f.Memory.Book.Orders.Single().Blcb);
        await f.Model.ReloadCommand.ExecuteAsync(null);
        var fresh = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CreateReportReviewAsync(Token));
        Assert.NotSame(review, fresh); Assert.Null(fresh.Error); Assert.Equal("37", candidate.Draft.Length);
    }

    [AvaloniaFact]
    public async Task RefreshingAnOpenReviewPreservesItsCandidatesWhenTheOrderChanged()
    {
        using var f = await WorkflowAutoRefreshTests.Fixture.Create(ready: true);
        await ProjectFixture.WriteAsync(f.Memory.Book.Orders.Single().Projects.Single(), length: 20000);
        var review = (await f.Model.CreateReportReviewAsync(Token))!; var candidate = Assert.Single(review.Candidates);
        candidate.Draft!.Length = "37"; var source = candidate.SelectedFile; var conflicts = candidate.Conflicts.ToArray();
        f.Change(o => o with { Blcb = "009337", Version = o.Version + 1 });
        await review.RefreshCommand.ExecuteAsync(null);
        Assert.Same(candidate, Assert.Single(review.Candidates)); Assert.Same(source, candidate.SelectedFile);
        Assert.Equal(conflicts, candidate.Conflicts); Assert.Equal("37", candidate.Draft.Length);
        Assert.Contains("Заявка изменена", review.Error); Assert.False(review.SaveCommand.CanExecute(null));
        Assert.Same(review, await f.Model.CreateReportReviewAsync(Token));
    }

    [AvaloniaFact]
    public async Task AReportDraftDoesNotPreventRefreshingAnotherOrder()
    {
        using var f = await WorkflowAutoRefreshTests.Fixture.Create(ready: true);
        var a = f.Memory.Book.Orders.Single(); var b = AnotherOrder(a);
        f.Memory.Book = f.Memory.Book with { Version = f.Memory.Book.Version + 1, Orders = [a, b] };
        await f.Model.ReloadCommand.ExecuteAsync(null); f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == a.Id);
        await ProjectFixture.WriteAsync(a.Projects.Single(), length: 20000);
        var review = (await f.Model.CreateReportReviewAsync(Token))!;
        Assert.Single(review.Candidates).Draft!.Length = "37"; f.Model.ReportReviewClosed();
        f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == b.Id);
        f.Change(o => o.Id == b.Id ? o with { Blcb = "009337", Version = o.Version + 1 } : o);
        Assert.Equal(WorkflowRefreshOutcome.Refreshed, await f.Model.RefreshAutomaticallyAsync(false, token: Token));
        Assert.Equal("009337", f.Model.Blcb); Assert.False(f.Model.IsCurrentCardStale); Assert.True(f.Model.HasPendingEdits);
        f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == a.Id);
        var reopened = (await f.Model.CreateReportReviewAsync(Token))!;
        Assert.Equal("37", Assert.Single(reopened.Candidates).Draft!.Length); Assert.Null(reopened.Error);
    }

    [AvaloniaFact]
    public async Task ChangingSelectionDuringReadDefersSnapshotThenRetainsTheSelectedOrderOutsideSearch()
    {
        using var f = await WorkflowAutoRefreshTests.Fixture.Create();
        var original = f.Memory.Book.Orders.Single(); var a = original with { Blcb = "ключ", Version = original.Version + 1 };
        var b = AnotherOrder(a);
        f.Memory.Book = f.Memory.Book with { Version = f.Memory.Book.Version + 1, Orders = [a, b] };
        await f.Model.ReloadCommand.ExecuteAsync(null); f.Model.Search = "ключ"; await f.Model.SearchDatabaseNowAsync();
        f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == a.Id); Assert.Equal(2, f.Model.Orders.Count);
        f.Change(o => o.Id == b.Id ? o with { Blcb = "больше не подходит", Version = o.Version + 1 } : o);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Transport.Delay = async request => { if (request.Operation == WorkflowAccessOperation.Read) { entered.TrySetResult(); await release.Task; } };
        var refresh = f.Model.RefreshAutomaticallyAsync(false, token: Token);
        try
        {
            await entered.Task.WaitAsync(Token);
            f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == b.Id); f.Model.Blcb = "мой ввод";
        }
        finally { release.TrySetResult(); }
        Assert.Equal(WorkflowRefreshOutcome.Deferred, await refresh);
        Assert.False(f.Model.IsCurrentOrderMissing, f.Model.CardReviewText); Assert.Equal(2, f.Model.Orders.Count);
        f.Transport.Delay = null;
        Assert.Equal(WorkflowRefreshOutcome.Refreshed, await f.Model.RefreshAutomaticallyAsync(false, token: Token));
        Assert.Contains(f.Memory.Book.Orders, o => o.Id == b.Id && !o.IsDeleted);
        Assert.True(f.Model.IsOrderOpen); Assert.Contains(b.DisplayNumber, f.Model.OrderTitle); Assert.Equal("мой ввод", f.Model.Blcb);
        Assert.False(f.Model.IsCurrentOrderMissing, f.Model.CardReviewText); Assert.True(f.Model.IsCurrentCardStale);
        Assert.False(f.Model.SaveHeaderCommand.CanExecute(null)); Assert.Null(f.Model.BackgroundRefreshError);
    }

    [AvaloniaTheory]
    [InlineData("add", false)]
    [InlineData("rename", true)]
    [InlineData("remove", false)]
    public async Task OpenProjectPlannerUpdatesItsMachinePickerWithoutDiscardingInput(string change, bool dark)
    {
        using var f = await WorkflowAutoRefreshTests.Fixture.Create();
        f.Preferences.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(f.Preferences, journalRefreshClock: new JournalClock(),
            journalSubscribe: (_, _, _) => Task.FromResult<IDisposable>(new JournalSubscription()))
        { DataContext = f.Model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show();
            for (var i = 0; i < 500 && f.Model.IsBusy; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10, Token); }
            Assert.False(f.Model.IsBusy);
            f.Model.AddProjectCommand.Execute(null); f.Model.PlanThickness = "6";
            var selected = f.Model.ProjectMachines.First(); f.Model.PlanMachine = selected; Dispatcher.UIThread.RunJobs();
            var picker = window.FindControl<ComboBox>("WorkflowProjectMachine")!;
            var machines = change switch
            {
                "add" => [..f.Memory.Book.Machines, selected with { Id = Guid.NewGuid(), Name = "Новый станок коллеги", LegacyKey = null }],
                "rename" => f.Memory.Book.Machines.Select(m => m.Id == selected.Id ? m with { Name = "Новое название" } : m).ToArray(),
                _ => f.Memory.Book.Machines.Where(m => m.Id != selected.Id).ToArray()
            };
            f.Memory.Book = f.Memory.Book with
            {
                Machines = machines, Version = f.Memory.Book.Version + 1,
                Rates = f.Memory.Book.Rates.Where(r => machines.Any(m => m.Id == r.MachineId)).ToArray()
            };
            f.Memory.BumpCatalogVersion();
            Assert.Equal(WorkflowRefreshOutcome.Refreshed, await f.Model.RefreshAutomaticallyAsync(false, token: Token));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(machines, picker.Items.Cast<WorkflowMachine>()); Assert.Equal("6", f.Model.PlanThickness); Assert.True(f.Model.IsPlanning);
            if (change == "remove") { Assert.Null(picker.SelectedItem); Assert.Null(f.Model.PlanMachine); Assert.False(f.Model.CanCreateProject); }
            else
            {
                Assert.Equal(selected.Id, f.Model.PlanMachine!.Id); Assert.Equal(f.Model.PlanMachine, picker.SelectedItem);
                Assert.Equal(machines.Single(m => m.Id == selected.Id), f.Model.PlanMachine);
            }
        }
        finally { window.Close(); }
    }

    private static WorkflowOrder AnotherOrder(WorkflowOrder original) => original with
    {
        Id = Guid.NewGuid(), Number = original.Number + 1,
        ReferenceNumber = original.CreatedAt.AddSeconds(1).ToLocalTime().ToString("yyyy-MM-dd-HHmmss"),
        Projects = [], Items = [], Specifications = []
    };
}
