using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowPeriodTests
{
    [Fact]
    public async Task TodayAndArbitraryDayUseLocalCreationDateAndSearchIsGlobalUntilDayIsChosen()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var todayId = Assert.Single(f.Model.Orders).Order.Id;
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var yesterday = DateTime.Today.AddDays(-1);
        // An offset which crosses a UTC day boundary: filtering must use the local creation day.
        var instant = new DateTimeOffset(yesterday.AddMinutes(5)).ToOffset(TimeSpan.FromHours(-8));
        var old = WorkflowChanges.CreateOrder(book, "ООО «Пример»", book.Employees[0].Id, book.Employees[1].Id,
            book.Employees[1].Id, instant, out _);
        await f.Store.WriteAsync(book.Version, _ => old, TestContext.Current.CancellationToken);
        await f.Model.ReloadCommand.ExecuteAsync(null);
        Assert.True(f.Model.IsTodaySelected); Assert.Equal(todayId, Assert.Single(f.Model.Orders).Order.Id);
        f.Model.FilterDate = yesterday.AddHours(15); Assert.Equal(yesterday, f.Model.FilterDate);
        var oldId = Assert.Single(f.Model.Orders).Order.Id; Assert.NotEqual(todayId, oldId);
        Assert.Null(f.Model.SelectedOrder); Assert.True(f.Model.IsOrderOpen);
        f.Model.Search = "Пример"; Assert.True(f.Model.IsAllDatesSelected); Assert.Equal(2, f.Model.Orders.Count);
        f.Model.FilterDate = yesterday; Assert.Equal(oldId, Assert.Single(f.Model.Orders).Order.Id);
        Assert.Equal("Пример", f.Model.Search);
        f.Model.AllDatesCommand.Execute(null); Assert.Equal("Пример", f.Model.Search); Assert.Equal(2, f.Model.Orders.Count);
        f.Model.TodayCommand.Execute(null); f.Model.TodayCommand.Execute(null);
        Assert.True(f.Model.IsTodaySelected); Assert.Single(f.Model.Orders);
        f.Model.Search = "совпадений нет"; Assert.True(f.Model.IsAllDatesSelected); Assert.True(f.Model.IsOrderListEmpty);
        Assert.Equal("Заявки не найдены", f.Model.EmptyOrderListText);
        f.Model.Search = ""; Assert.True(f.Model.IsAllDatesSelected); Assert.Equal(2, f.Model.Orders.Count);
    }

    [Fact]
    public async Task PeriodOnlyChangesLeftListAndPreservesDirtyCardPlanningLetterAndNewOrderFormWithoutIo()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); await f.AddAsync();
        f.Model.AddProjectCommand.Execute(null); f.Model.PlanThickness = "18"; var machine = f.Model.PlanMachine;
        f.Model.AddLegacySpecificationDraft(); var draft = f.Model.SelectedDraft!; draft.Comment = "Мой ввод";
        f.Model.ResultText = "Ручное письмо"; f.Model.Blcb = "77";
        var title = f.Model.OrderTitle; var bytes = await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken);
        // Even an unavailable file store must not affect a local period switch.
        using (var lease = new FileStream(f.Store.FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            f.Model.FilterDate = DateTime.Today.AddDays(-30);
            Assert.Empty(f.Model.Orders); Assert.Null(f.Model.SelectedOrder); Assert.True(f.Model.IsOrderListEmpty);
            Assert.Equal("Нет заявок за " + f.Model.FilterDate.Value.ToString("dd.MM.yyyy"), f.Model.EmptyOrderListText);
            Assert.Equal(title, f.Model.OrderTitle); Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("Мой ввод", draft.Comment);
            Assert.Equal("77", f.Model.Blcb); Assert.Equal("Ручное письмо", f.Model.ResultText);
            Assert.True(f.Model.IsPlanning); Assert.Equal("18", f.Model.PlanThickness); Assert.Same(machine, f.Model.PlanMachine);
            f.Model.AllDatesCommand.Execute(null); Assert.Single(f.Model.Orders); Assert.Equal(title, f.Model.OrderTitle);
            f.Model.NewOrderCommand.Execute(null); f.Model.NewCustomer = "Новая компания"; f.Model.NewManager = "Петров";
            f.Model.TodayCommand.Execute(null); Assert.True(f.Model.IsCreating); Assert.Equal("Новая компания", f.Model.NewCustomer);
            Assert.Equal("Петров", f.Model.NewManager); f.Model.CancelNewOrderCommand.Execute(null);
        }
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Store.FilePath, TestContext.Current.CancellationToken)); Assert.Null(f.Model.Error);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.True(f.Model.HasPendingEdits);
    }

    [Fact]
    public async Task CreatedOrderIsRevealedWhenViewingAnotherDayAndReloadRetainsChosenPeriod()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        f.Model.FilterDate = DateTime.Today.AddYears(-1); Assert.Empty(f.Model.Orders);
        f.Model.NewOrderCommand.Execute(null); f.Model.NewCustomer = "Новая компания"; f.Model.NewManager = "Петров";
        await f.Model.CreateOrderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        Assert.True(f.Model.IsAllDatesSelected); Assert.Equal(2, f.Model.Orders.Count); Assert.Equal("Новая компания", f.Model.SelectedOrder!.Customer);
        f.Model.FilterDate = DateTime.Today.AddDays(-1); await f.Model.ReloadCommand.ExecuteAsync(null);
        Assert.Empty(f.Model.Orders); Assert.Equal(DateTime.Today.AddDays(-1), f.Model.FilterDate);
        f.Model.BackCommand.Execute(null); f.Model.AllDatesCommand.Execute(null);
        Assert.Null(f.Model.SelectedOrder); Assert.False(f.Model.IsOrderOpen);
        f.Model.SelectedOrder = f.Model.Orders[0]; Assert.True(f.Model.IsOrderOpen);
    }

    [Fact]
    public void CalendarBrowsesWithoutSelectionCommitsOnlyDaysAndHandlesLeapYearAndLimits()
    {
        var calendar = new WorkflowCalendarViewModel(); calendar.SetSelection(new(2024, 2, 29)); calendar.Open();
        Assert.Equal("Февраль 2024", calendar.Title); Assert.Equal(42, calendar.Cells.Count);
        Assert.Equal(DayOfWeek.Monday, calendar.Cells[0].Date.DayOfWeek);
        Assert.Single(calendar.Cells, c => c.IsSelected);
        calendar.Move(1); Assert.Equal("Март 2024", calendar.Title); Assert.Equal("29.02.2024", calendar.Caption);
        calendar.Broaden(); Assert.Equal(12, calendar.Cells.Count); Assert.False(calendar.IsDays);
        calendar.Broaden(); calendar.Move(-1); Assert.Equal("29.02.2024", calendar.Caption);
        Assert.Null(calendar.Choose(calendar.Cells[0])); Assert.Null(calendar.Choose(calendar.Cells[1])); Assert.True(calendar.IsDays);
        var day = calendar.Cells.First(c => !c.IsOutside); Assert.Equal(day.Date, calendar.Choose(day));
        calendar.SetSelection(DateTime.MinValue); calendar.Open(); Assert.False(calendar.CanPrevious);
        calendar.Move(-1); Assert.Equal("Январь 0001", calendar.Title);
        calendar.SetSelection(DateTime.MaxValue.Date); calendar.Open(); Assert.False(calendar.CanNext);
        calendar.Move(1); Assert.Equal("Декабрь 9999", calendar.Title);
        Assert.Single(calendar.Cells, c => c.IsSelected); Assert.DoesNotContain(calendar.Cells, c => !c.IsEnabled && c.IsSelected);
        calendar.SetSelection(null); calendar.Open(); Assert.Equal("Выбрать дату", calendar.Caption);
        Assert.Single(calendar.Cells, c => c.IsToday); Assert.DoesNotContain(calendar.Cells, c => c.IsSelected);
    }
}
