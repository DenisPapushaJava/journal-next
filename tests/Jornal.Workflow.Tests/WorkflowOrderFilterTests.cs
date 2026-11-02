using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowOrderFilterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SavedCompositionAndResponsibleEmployeeDefineTheFiltersAndChangedHandoffsStayInWork()
    {
        using var f = new ShopFixture(bending: true); var order = f.Order with { CreatedById = f.Book.Employees[0].Id };
        Assert.True(new WorkflowOrderFilter(order.TechnologistId, WithBending: true).Matches(order));
        Assert.False(new WorkflowOrderFilter(order.CreatedById).Matches(order));
        Assert.False(new WorkflowOrderFilter(Guid.Empty).Matches(order));
        Assert.False(new WorkflowOrderFilter(WithBending: true, WithChamfer: true).Matches(order));
        var another = order.Projects[0] with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid() };
        var chamfer = order.Specifications[0] with { Id = another.Id, Current = order.Specifications[0].Current with
            { Parameters = order.Specifications[0].Current.Parameters with { HasBending = false, HasChamfer = true } } };
        order = order with { Projects = [.. order.Projects, another], Items = [.. order.Items, new(another.ItemId, 2, another.Id)],
            Specifications = [.. order.Specifications, chamfer], ShopHandoff = new(0, order.TechnologistId, DateTimeOffset.UtcNow) };
        Assert.False(order.HasCurrentShopHandoff);
        Assert.True(new WorkflowOrderFilter(Work: WorkflowWorkFilter.InWork, WithBending: true, WithChamfer: true).Matches(order));
        Assert.False(new WorkflowOrderFilter(Work: WorkflowWorkFilter.NotSent).Matches(order));
        Assert.False(new WorkflowOrderFilter(WithChamfer: true).Matches(order with
            { Items = order.Items.Select(i => i with { IsIncluded = i.Id != another.ItemId }).ToArray() }));
        var replacement = another with { Id = Guid.NewGuid(), Variant = 2 };
        var changed = order with { Projects = [.. order.Projects, replacement], Items = order.Items.Select(i =>
            i.Id == another.ItemId ? i with { CurrentProjectId = replacement.Id } : i).ToArray() };
        Assert.False(new WorkflowOrderFilter(WithChamfer: true).Matches(changed)); // Only the old variant has chamfer.
        Assert.False(new WorkflowOrderFilter(WithBending: true).Matches(order with
            { Projects = order.Projects.Select(p => p with { DeletedAt = DateTimeOffset.UtcNow }).ToArray() }));
        Assert.True(new WorkflowOrderFilter(WithBending: true).Matches(order with { Projects = [], Items = [] })); // Legacy saved calculations.
    }

    [Fact]
    public void SqlFiltersAreParameterizedAndApplyToCountBeforeTheSeekCursorIncludingExactNumbers()
    {
        var owner = Guid.NewGuid(); var from = DateTimeOffset.UtcNow;
        var filters = new WorkflowOrderFilter(owner, WorkflowWorkFilter.InWork, true, true);
        var query = new WorkflowReadQuery(from, from.AddDays(1), "2026-10-31-110032", 2, from, 99, Filter: filters);
        var plan = WorkflowMdbQueries.Build(query);
        Assert.Contains("TOP 3", plan.SelectIds); Assert.Contains("o.[technologist_id]=?", plan.Count);
        Assert.Contains("[order_shop_handoffs]", plan.Count); Assert.Contains("[has_bending]<>0", plan.Count); Assert.Contains("[has_chamfer]<>0", plan.Count);
        Assert.Contains("[is_included]<>0", plan.Count); Assert.Contains("sel.[project_id]=fs.[id]", plan.Count);
        Assert.Contains("fp.[deleted_at] IS NULL", plan.Count); Assert.Contains("NOT EXISTS", plan.Count);
        Assert.Contains("o.[created_at]>=?", plan.Count); Assert.Contains("o.[reference_number]=?", plan.Count);
        Assert.DoesNotContain(owner.ToString(), plan.Count); Assert.DoesNotContain("[updated_at]<?", plan.Count);
        Assert.Equal(plan.Count.Count(c => c == '?'), plan.CountParameters.Length);
        Assert.Equal(plan.SelectIds.Count(c => c == '?'), plan.Parameters.Length);
        Assert.Equal(owner, plan.CountParameters[2].Value);
        var trash = WorkflowMdbQueries.Build(query, trash: true);
        Assert.DoesNotContain("[has_bending]", trash.Count); Assert.DoesNotContain("[technologist_id]=?", trash.Count);
        Assert.Throws<WorkflowValidationException>(() => WorkflowMdbQueries.Build(query with { Filter = filters with { Work = (WorkflowWorkFilter)999 } }));
        Assert.Equal(WorkflowMdbQueries.Build(new()).SelectIds, WorkflowMdbQueries.Build(new(Filter: WorkflowOrderFilter.All)).SelectIds);
        var old = JsonSerializer.Deserialize<WorkflowReadQuery>("{\"Search\":\"Ст3\"}")!; Assert.Null(old.Filter);
        var roundtrip = JsonSerializer.Deserialize<WorkflowReadQuery>(JsonSerializer.Serialize(query)); Assert.Equal(filters, roundtrip!.Filter);
    }

    [Fact]
    public async Task PagingFiltersTheEntireDatasetAndKeepsAnOutsideCardWithoutCountingIt()
    {
        using var f = new ShopFixture(bending: true);
        var template = f.Order; var at = DateTimeOffset.UtcNow;
        var orders = Enumerable.Range(1, 230).Select(i => template with { Id = Guid.NewGuid(), Number = i,
            ReferenceNumber = at.AddSeconds(-i).ToString("yyyy-MM-dd-HHmmss"), UpdatedAt = at.AddSeconds(-i),
            TechnologistId = i % 3 == 0 ? template.TechnologistId : f.Book.Employees[0].Id }).ToArray();
        var memory = new MemoryMdb { Book = f.Book with { Orders = orders } };
        var outside = orders[0]; var criteria = new WorkflowOrderFilter(template.TechnologistId, WithBending: true);
        var store = new WorkflowMdbStore(Path.Combine(f.Root, "shared.mdb"), memory, memory.Id)
            { Query = new(Limit: 25, RetainIds: [outside.Id], Filter: criteria) };
        var book = await store.ReadAsync(Token); Assert.Equal(26, book.Orders.Length); Assert.Equal(76, store.TotalCount);
        var ids = book.Orders.Where(criteria.Matches).Select(o => o.Id).ToHashSet();
        while (store.HasMore)
        {
            book = await store.ReadMoreAsync(Token);
            ids.UnionWith(book.Orders.Where(criteria.Matches).Select(o => o.Id));
        }
        Assert.Equal(76, ids.Count); Assert.Contains(book.Orders, o => o.Id == outside.Id);
        var prepared = await store.PrepareBackgroundReadAsync(Token);
        store.Query = store.Query with { Filter = criteria with { Work = WorkflowWorkFilter.InWork } };
        Assert.False(store.HasMore); Assert.False(store.TryAcceptBackgroundRead(prepared));
        Assert.DoesNotContain((await store.ReadAsync(Token)).Orders, store.Query.Filter!.Matches); Assert.Equal(0, store.TotalCount);
    }

    [AvaloniaFact]
    public async Task LocalFiltersCombineWithDateAndSearchAndNeverDiscardOrSaveTheOpenDraft()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); var review = await f.ReviewAsync(); review.Selected!.Draft!.HasBending = true;
        await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error);
        var id = f.Model.SelectedOrder!.Order.Id; var date = f.Model.FilterDate;
        var bytes = await File.ReadAllBytesAsync(f.Store.FilePath, Token); var draft = f.Model.SelectedDraft!; draft.Comment = "Черновик";
        f.Model.OnlyMyOrders = true; f.Model.OrdersWithBending = true; Assert.Single(f.Model.Orders);
        f.Model.Search = "Пример"; Assert.Equal(date, f.Model.FilterDate); Assert.Single(f.Model.Orders);
        f.Model.FilterDate = DateTime.Today.AddDays(1); Assert.Empty(f.Model.Orders); Assert.True(f.Model.IsCardOutsideFilter);
        f.Model.AllDatesCommand.Execute(null); Assert.True(f.Model.OnlyMyOrders); Assert.True(f.Model.OrdersWithBending); Assert.Single(f.Model.Orders);
        f.Model.OrdersWithChamfer = true; Assert.Empty(f.Model.Orders); Assert.True(f.Model.IsCardVisible); Assert.Same(draft, f.Model.SelectedDraft);
        f.Model.ClearQuickOrderFiltersCommand.Execute(null); Assert.Equal("Пример", f.Model.Search); Assert.Null(f.Model.FilterDate);
        Assert.Equal(id, Assert.Single(f.Model.Orders).Order.Id); Assert.False(f.Model.HasOrderFilters);
        f.Model.FilterDate = date; f.Model.OnlyMyOrders = true;
        f.Model.CurrentEmployee = f.Model.Employees.First(e => e.CanWork && e.Id != f.Model.CurrentEmployee!.Id); Assert.Empty(f.Model.Orders);
        f.Model.CurrentEmployee = null; Assert.False(f.Model.CanFilterMyOrders); Assert.Empty(f.Model.Orders);
        f.Model.ResetOrderFiltersCommand.Execute(null); Assert.Null(f.Model.FilterDate); Assert.Empty(f.Model.Search); Assert.False(f.Model.HasOrderFilters);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("Черновик", draft.Comment); Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
    }

    [AvaloniaFact]
    public async Task MdbViewModelKeepsFiltersOnRefreshAndRejectsLateResponsesForOldConditions()
    {
        using var f = await WorkflowAutoRefreshTests.Fixture.Create(ready: true);
        f.Change(o => o with { Specifications = o.Specifications.Select(s => s with { Current = s.Current with
            { Parameters = s.Current.Parameters with { HasBending = true } } }).ToArray(), Version = o.Version + 1 });
        f.Model.OnlyMyOrders = true; f.Model.OrdersWithBending = true; await f.Model.SearchDatabaseNowAsync();
        Assert.Single(f.Model.Orders); Assert.Equal(f.Model.CurrentEmployee!.Id, f.Store.Query.Filter!.TechnologistId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        f.Transport.Delay = async r => { if (r.Operation == WorkflowAccessOperation.Read && Interlocked.Increment(ref reads) == 1) { entered.TrySetResult(); await release.Task; } };
        var old = f.Model.SearchDatabaseNowAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        f.Model.OrdersWithChamfer = true;
        release.SetResult(); await old; f.Transport.Delay = null;
        await f.Model.SearchDatabaseNowAsync(); Assert.Empty(f.Model.Orders); Assert.Equal(0, f.Store.TotalCount);
        Assert.True(f.Model.OnlyMyOrders); Assert.True(f.Model.OrdersWithBending); Assert.True(f.Model.OrdersWithChamfer);
        f.Change(o => o with { Specifications = o.Specifications.Select(s => s with { Current = s.Current with
            { Parameters = s.Current.Parameters with { HasChamfer = true } } }).ToArray(), Version = o.Version + 1 });
        Assert.Equal(WorkflowRefreshOutcome.Refreshed, await f.Model.RefreshAutomaticallyAsync(false, token: Token));
        Assert.Single(f.Model.Orders); await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Single(f.Model.Orders); Assert.True(f.Model.OnlyMyOrders);
    }

    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1480)] [InlineData(true, 1480)]
    public async Task FilterPopupIsCompactAndEmptyResultsKeepTheOpenCard(bool dark, int width)
    {
        using var log = new WorkflowWindowTests.BindingLog(); using var f = new ProjectFixture(); await f.InitializeAsync();
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Pump();
            var button = window.FindControl<JournalButton>("WorkflowOrderFiltersButton")!;
            var quickReset = window.FindControl<JournalButton>("WorkflowClearOrderFilters")!;
            var flyout = Assert.IsType<Flyout>(button.Flyout);
            var panel = Assert.IsType<StackPanel>(flyout.Content);
            var summary = window.FindControl<TextBlock>("WorkflowSearchSummary")!;
            var grid = window.FindControl<DataGrid>("WorkflowOrdersGrid")!; var top = grid.Bounds.Top;
            Assert.Equal("Фильтры", button.Label); Assert.False(quickReset.IsVisible);
            Click(button); await Task.Delay(220, Token); Pump();
            Assert.True(flyout.IsOpen); Assert.Equal(272, panel.Bounds.Width);
            Click(panel.FindControl<CheckBox>("WorkflowOnlyMyOrders")!); Assert.True(f.Model.OnlyMyOrders); Assert.Single(f.Model.Orders);
            Click(panel.FindControl<CheckBox>("WorkflowOrdersWithBending")!); Assert.Empty(f.Model.Orders);
            Assert.Equal("Фильтры · 2", button.Label); Assert.Contains("primary", button.Classes);
            Assert.True(quickReset.IsVisible); Assert.Equal(24, quickReset.Bounds.Width);
            Assert.True(quickReset.TranslatePoint(default, window)!.Value.X + quickReset.Bounds.Width <= grid.TranslatePoint(default, window)!.Value.X + grid.Bounds.Width);
            Assert.Equal(top, grid.Bounds.Top); Assert.True(f.Model.IsCardVisible);
            Assert.True(window.FindControl<Button>("WorkflowClearEmptyFilters")!.IsEffectivelyVisible);
            SaveFrame(window, $"filters-open-{width}-{(dark ? "dark" : "light")}");
            Assert.True(panel.GetVisualDescendants().OfType<CheckBox>().All(c => c.IsEffectivelyVisible));
            Assert.InRange(summary.Bounds.Width, 1, grid.Bounds.Width - button.Bounds.Width);
            flyout.Hide(); Pump();
            f.Model.Search = "Пример"; var date = f.Model.FilterDate;
            Click(window.FindControl<Button>("WorkflowClearEmptyFilters")!); Assert.Single(f.Model.Orders);
            Assert.Equal(date, f.Model.FilterDate); Assert.Equal("Пример", f.Model.Search); Assert.False(f.Model.HasOrderFilters);
            SaveFrame(window, $"filters-reset-{width}-{(dark ? "dark" : "light")}");
            f.Model.OnlyMyOrders = true; f.Model.OrdersWithBending = true; f.Model.Blcb = "Несохранённый ввод"; Pump();
            Assert.True(quickReset.IsVisible); Assert.False(flyout.IsOpen);
            SaveFrame(window, $"filters-quick-reset-{width}-{(dark ? "dark" : "light")}");
            Click(quickReset); Assert.False(flyout.IsOpen); Assert.False(f.Model.HasOrderFilters); Assert.False(quickReset.IsVisible);
            Assert.True(button.IsFocused); Assert.Equal("Пример", f.Model.Search); Assert.Equal(date, f.Model.FilterDate);
            Assert.Equal("Несохранённый ввод", f.Model.Blcb); Assert.True(f.Model.IsCardVisible); Assert.Single(f.Model.Orders);
            Assert.Empty(log.Errors);
        }
        finally { ((Flyout?)window.FindControl<JournalButton>("WorkflowOrderFiltersButton")?.Flyout)?.Hide(); WorkflowTestWindows.Close(window); }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, Token); Pump(); } Assert.True(done()); }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void SaveFrame(TopLevel window, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } directory) return;
        Directory.CreateDirectory(directory); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name + ".png"), new PngBitmapEncoderOptions());
    }
}
