using System.Text.Json;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowNavigationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task Ready(ProjectFixture f)
    { await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync(); }

    [Fact]
    public async Task EmptyFiltersKeepOpenCardAndInputWithoutWritingThenResetReselectsIt()
    {
        using var f = new ProjectFixture(); await Ready(f); var id = f.Model.SelectedOrder!.Order.Id;
        var before = JsonSerializer.Serialize(await f.Store.ReadAsync(Token));
        f.Model.Blcb = "123"; f.Model.SelectedDraft!.Length = "17"; f.Model.ResultText = "Ручной результат";
        f.Model.Search = "Ничего такого нет";
        Assert.Empty(f.Model.Orders); Assert.True(f.Model.IsCardVisible); Assert.True(f.Model.IsCardOutsideFilter); Assert.Null(f.Model.SelectedOrder);
        f.Model.FilterDate = DateTime.Today.AddDays(10); Assert.True(f.Model.IsCardOutsideFilter);
        f.Model.ResetOrderFiltersCommand.Execute(null);
        Assert.Equal(id, f.Model.SelectedOrder!.Order.Id); Assert.False(f.Model.IsCardOutsideFilter);
        Assert.Equal("123", f.Model.Blcb); Assert.Equal("17", f.Model.SelectedDraft.Length); Assert.Equal("Ручной результат", f.Model.ResultText);
        Assert.Equal(before, JsonSerializer.Serialize(await f.Store.ReadAsync(Token)));
    }

    [Fact]
    public async Task ExplicitCleanCloseLeavesNoSelectionEvenAfterResetAndReload()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var request = f.Model.PrepareClose(false)!; Assert.False(request.NeedsConfirmation);
        Assert.True(f.Model.CloseCard(request)); Assert.True(f.Model.IsNoCardSelected); Assert.Null(f.Model.SelectedOrder);
        f.Model.ResetOrderFiltersCommand.Execute(null); Assert.Null(f.Model.SelectedOrder);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.True(f.Model.IsNoCardSelected); Assert.Null(f.Model.SelectedOrder);
        f.Model.SelectedOrder = f.Model.Orders.First(); Assert.True(f.Model.IsCardVisible);
    }

    [Fact]
    public async Task ChangedInputInvalidatesAnAlreadyShownCloseConfirmation()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); f.Model.Blcb = "123";
        var request = f.Model.PrepareClose(false)!; Assert.True(request.NeedsConfirmation); Assert.True(request.CanSave);
        f.Model.Blcb = "456";
        Assert.False(f.Model.IsCloseRequestCurrent(request)); Assert.False(f.Model.CloseCard(request));
        Assert.False(await f.Model.SaveCloseDataAsync(request)); Assert.Equal("456", f.Model.Blcb); Assert.True(f.Model.IsCardVisible);
        Assert.Null((await f.Store.ReadAsync(Token)).Orders.Single().Blcb);
    }

    [Fact]
    public async Task SaveCloseSavesHeaderAndEveryDirtyCalculationAndDoesNotCreateProjects()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var a = await f.AddAsync(); var b = await f.AddAsync();
        await ProjectFixture.WriteAsync(a); await ProjectFixture.WriteAsync(b); await f.AcceptAsync();
        f.Model.Blcb = "789"; f.Model.Drafts[0].Length = "17"; f.Model.Drafts[1].Length = "19";
        var request = f.Model.PrepareClose(false)!; Assert.True(request.CanSave);
        Assert.True(await f.Model.SaveCloseDataAsync(request));
        var clean = f.Model.PrepareClose(false)!; Assert.False(clean.NeedsConfirmation); Assert.True(f.Model.CloseCard(clean));
        var order = (await f.Store.ReadAsync(Token)).Orders.Single();
        Assert.Equal("789", order.Blcb); Assert.Equal(2, order.Projects.Length);
        Assert.Equal(new decimal[] { 17, 19 }, order.Specifications.Select(s => s.Current.Parameters.CutLengthMeters).Order().ToArray());
    }

    [Fact]
    public async Task SaveFailureKeepsInputAndDoesNotCloseOrDiscard()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); f.Model.Blcb = "123";
        var request = f.Model.PrepareClose(false)!;
        using (File.Open(f.Store.FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.False(await f.Model.SaveCloseDataAsync(request));
        Assert.NotNull(f.Model.Error); Assert.True(f.Model.IsCardVisible); Assert.Equal("123", f.Model.Blcb);
        Assert.Null((await f.Store.ReadAsync(Token)).Orders.Single().Blcb);
    }

    [Fact]
    public async Task ExitIncludesManualTextInInactiveCardsWithoutTreatingItAsBusinessEdits()
    {
        using var f = new ProjectFixture(); await Ready(f); f.Model.ResultText = "Не потерять мой текст";
        Assert.True(f.Model.SelectedOrder!.HasDraft);
        Assert.False(f.Model.HasPendingEdits); Assert.True(f.Model.PrepareClose(false)!.NeedsConfirmation);
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Другой"; f.Model.NewCustomer = "Вторая заявка";
        await f.Model.CreateOrderCommand.ExecuteAsync(null);
        var request = f.Model.PrepareClose(true)!; Assert.True(request.NeedsConfirmation); Assert.False(request.CanSave);
        Assert.Contains("Других заявок", request.Message); Assert.Contains("Ручной текст", request.Message);
        Assert.False(f.Model.PrepareClose(false)!.NeedsConfirmation);
    }

    [Fact]
    public async Task ShopTextSurvivesNavigationAndNewVersionRequiresReviewInsteadOfDiscardingIt()
    {
        using var f = new ProjectFixture(); await Ready(f); f.Model.Blcb = "100"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        var id = f.Model.SelectedOrder!.Order.Id; var text = f.Model.CuttingTaskText + "Ручное уточнение";
        f.Model.CuttingTaskText = text; Assert.True(f.Model.PrepareClose(false)!.NeedsConfirmation);
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Другой"; f.Model.NewCustomer = "Вторая";
        await f.Model.CreateOrderCommand.ExecuteAsync(null); Assert.True(f.Model.HasUnfinishedTaskText);
        f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == id);
        Assert.Equal(text, f.Model.CuttingTaskText); Assert.True(f.Model.CanCopyCuttingTaskText);
        f.Model.Blcb = "101"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        Assert.Equal(text, f.Model.CuttingTaskText); Assert.True(f.Model.IsCuttingTaskTextStale);
        Assert.False(f.Model.CanCopyCuttingTaskText); Assert.False(f.Model.CanSendToWork);
        var copied = false; await f.Model.CopyCuttingTaskAsync(_ => { copied = true; return Task.CompletedTask; }); Assert.False(copied);
        f.Model.ResetCuttingTaskTextCommand.Execute(null); Assert.False(f.Model.HasCuttingTaskTextEdits); Assert.True(f.Model.CanCopyCuttingTaskText);
    }

    [Fact]
    public async Task ClosingNewOrderOrPlanningDoesNotCreateDatabaseRowsOrFolders()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var before = JsonSerializer.Serialize(await f.Store.ReadAsync(Token));
        f.Model.AddProjectCommand.Execute(null); f.Model.PlanThickness = "6";
        var request = f.Model.PrepareClose(false)!; Assert.True(request.NeedsConfirmation); Assert.False(request.CanSave);
        Assert.True(f.Model.CloseCard(request)); Assert.Equal(before, JsonSerializer.Serialize(await f.Store.ReadAsync(Token)));
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Менеджер"; f.Model.NewCustomer = "Не создавать";
        request = f.Model.PrepareClose(false)!; Assert.True(request.NeedsConfirmation); Assert.False(request.CanSave);
        Assert.True(f.Model.CloseCard(request)); Assert.True(f.Model.IsNoCardSelected);
        Assert.Equal(before, JsonSerializer.Serialize(await f.Store.ReadAsync(Token)));
    }

    [Fact]
    public async Task ExitStillProtectsReportReviewWhenAnEmptyNewOrderFormIsOpen()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.ReviewAsync();
        f.Model.NewOrderCommand.Execute(null); Assert.True(f.Model.IsCreating);
        var request = f.Model.PrepareClose(true)!; Assert.True(request.NeedsConfirmation); Assert.False(request.CanSave);
        Assert.Contains("подборе спецификаций", request.Message);
    }
}
