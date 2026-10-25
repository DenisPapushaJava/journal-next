using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Desktop.ViewModels;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowCardNavigationTests
{
    [Fact]
    public async Task UpdateRestartPreservesLettersFromAllVisitedCardsWithoutRestoringOldOrderData()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        await ProjectFixture.WriteAsync(p); await f.AcceptAsync();
        var firstId = f.Model.SelectedOrder!.Order.Id; f.Model.ResultText = "Первое письмо\n";
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Иванов"; f.Model.NewCustomer = "Второй заказчик"; await f.Model.CreateOrderCommand.ExecuteAsync(null);
        f.Model.ResultText = "Второе письмо\n"; Assert.False(f.Model.HasPendingEdits);
        var store = new WorkflowRestartStore(Path.Combine(f.DirectoryPath, "restart.json"));
        store.Save(f.Model.CreateRestartDraft());
        var restart = store.Take()!; Assert.Equal(2, restart.Results!.Length);
        var model = new WorkflowViewModel(f.Store, new AppUpdatesViewModel(new WorkflowAppUpdater()));
        try
        {
            await model.ReloadCommand.ExecuteAsync(null); model.RestoreRestartDraft(restart);
            Assert.Equal("Второе письмо\n", model.ResultText);
            model.SelectedOrder = model.Orders.Single(o => o.Order.Id == firstId);
            Assert.Equal("Первое письмо\n", model.ResultText); Assert.Single(model.Drafts);
            Assert.False(model.HasPendingEdits); Assert.Equal(1105, model.SelectedDraft!.Quote!.TotalCost);
        }
        finally { model.Detach(); }
    }

    [Fact]
    public async Task SwitchingOrdersKeepsDraftsHeaderLetterAndPendingUpdateGuardWithoutWriting()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var firstId = Assert.Single(f.Model.Orders).Order.Id; var draft = f.Model.SelectedDraft!;
        draft.Length = "17"; draft.Coefficient = "1,3"; draft.Comment = "Ручная правка"; draft.HasBending = true;
        f.Model.Blcb = "55"; f.Model.ManagerName = "Новый несохранённый менеджер"; f.Model.ResultText = "Ручной текст письма\n";
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Иванов"; f.Model.NewCustomer = "ООО «Вторая заявка»";
        await f.Model.CreateOrderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        Assert.True(f.Model.HasPendingEdits); // An inactive dirty card must prevent an update restart.
        Assert.True(f.Model.Orders.Single(o => o.Order.Id == firstId).HasDraft);
        var saved = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single(o => o.Id == firstId);
        Assert.Null(saved.Blcb); Assert.Equal(11, saved.Specifications[0].Current.Parameters.CutLengthMeters);
        Assert.Null(saved.Specifications[0].Current.Parameters.Comment);
        f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == firstId);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal("17", draft.Length); Assert.Equal("1,3", draft.Coefficient);
        Assert.Equal("55", f.Model.Blcb); Assert.Equal("Новый несохранённый менеджер", f.Model.ManagerName);
        Assert.Equal("Ручной текст письма\n", f.Model.ResultText); Assert.True(draft.HasBending);
        await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error); Assert.False(f.Model.HasPendingEdits);
        saved = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single(o => o.Id == firstId);
        Assert.Equal(2216.5m, saved.CuttingTotal); Assert.Equal("55", saved.Blcb);
    }

    [Fact]
    public async Task CancellingNewOrderRestoresTheExistingCardAndItsUnsavedFields()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        f.Model.Blcb = "123"; var manager = f.Model.ManagerName; var technologist = f.Model.Technologist;
        f.Model.NewOrderCommand.Execute(null); f.Model.ManagerName = "Несохранённый менеджер"; f.Model.Technologist = f.Model.Employees.First();
        f.Model.NewManager = "Иванов"; f.Model.NewCustomer = "Не сохранять"; Assert.False(f.Model.IsCardVisible);
        f.Model.CancelNewOrderCommand.Execute(null);
        Assert.True(f.Model.IsCardVisible); Assert.Equal("123", f.Model.Blcb);
        Assert.Equal(manager, f.Model.ManagerName); Assert.Equal(technologist, f.Model.Technologist);
        Assert.True(f.Model.HasPendingEdits); Assert.Single((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders);
    }

    [Fact]
    public async Task InactiveDraftRetainsItsOriginalVersionAndCannotOverwriteAnotherClientsChange()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        await ProjectFixture.WriteAsync(p); await f.AcceptAsync(); var firstId = f.Model.SelectedOrder!.Order.Id;
        f.Model.SelectedDraft!.Comment = "Моя несохранённая правка";
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Иванов"; f.Model.NewCustomer = "Другой заказчик";
        await f.Model.CreateOrderCommand.ExecuteAsync(null);
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var first = book.Orders.Single(o => o.Id == firstId); var prior = first.Specifications[0].Current;
        await f.Store.WriteOrderAsync(firstId, first.Version, w => WorkflowChanges.SaveSpecification(w, firstId, first.Version,
            first.Specifications[0].Id, prior.Parameters with { Comment = "Правка коллеги" }, prior.DocumentNumber, prior.DocumentDate,
            prior.AuthorId, DateTimeOffset.UtcNow, prior.SourcePath, prior.ImportProfile), TestContext.Current.CancellationToken);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == firstId);
        Assert.Equal("Моя несохранённая правка", f.Model.SelectedDraft!.Comment); Assert.NotNull(f.Model.Error);
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.NotNull(f.Model.Error);
        var actual = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single(o => o.Id == firstId);
        Assert.Equal("Правка коллеги", actual.Specifications[0].Current.Parameters.Comment);
        Assert.Single(actual.Specifications); Assert.Equal("Моя несохранённая правка", f.Model.SelectedDraft.Comment);
    }

    [Fact]
    public async Task SearchDoesNotReplaceAnOpenDirtyCardAndPlanningSurvivesNavigation()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var firstId = f.Model.SelectedOrder!.Order.Id;
        f.Model.AddProjectCommand.Execute(null); f.Model.PlanMachine = f.Model.ProjectMachines.Single(m => m.Name == "HansLaser"); f.Model.PlanThickness = "2";
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Иванов"; f.Model.NewCustomer = "Другая заявка"; await f.Model.CreateOrderCommand.ExecuteAsync(null);
        f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == firstId);
        Assert.True(f.Model.IsPlanning); Assert.Equal("HansLaser", f.Model.PlanMachine!.Name); Assert.Equal("2", f.Model.PlanThickness);
        f.Model.Search = "Другая заявка";
        Assert.Null(f.Model.SelectedOrder); Assert.Contains("Пример", f.Model.OrderTitle); Assert.True(f.Model.IsPlanning);
        f.Model.SelectedOrder = Assert.Single(f.Model.Orders); Assert.Contains("Другая заявка", f.Model.OrderTitle);
        f.Model.Search = ""; f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == firstId);
        Assert.True(f.Model.IsPlanning); Assert.Equal("HansLaser", f.Model.PlanMachine!.Name);
        f.Model.CancelPlanningCommand.Execute(null); Assert.False(f.Model.HasPendingEdits);
    }
}
