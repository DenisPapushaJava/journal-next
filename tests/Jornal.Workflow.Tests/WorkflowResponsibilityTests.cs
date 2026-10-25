using System.Text.Json;
using Jornal.Workflow.Core;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowResponsibilityTests
{
    [Fact]
    public async Task SeparateChangeKeepsHeaderDraftCalculationLetterAndCreator()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var before = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single();
        var draft = f.Model.SelectedDraft!; var row = f.Model.SelectedOrder!; var projectRow = f.Model.SelectedProject!;
        var savedCalculation = f.Model.SelectedDraft?.Saved;
        draft.Comment = "Ручная правка"; f.Model.Blcb = "Не сохранено"; f.Model.ManagerName = "Несохранённый менеджер";
        f.Model.ResultText = "Ручное письмо"; var actor = f.Model.CurrentEmployee!;
        var target = f.Model.Employees.First(); var request = f.Model.CreateResponsibleChange()!;
        await f.Model.ChangeResponsibleAsync(request, target.Id); Assert.Null(f.Model.Error);
        var after = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single();
        Assert.Equal(target.Id, after.TechnologistId); Assert.Equal(target.Id, f.Model.Technologist!.Id);
        Assert.Equal(before.ManagerId, after.ManagerId); Assert.Equal(before.Blcb, after.Blcb);
        Assert.Equal(before.CreatedById, after.CreatedById); Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(actor, f.Model.CurrentEmployee); Assert.Equal(before.CreatedById, after.CreatedById);
        Assert.Equal(JsonSerializer.Serialize(before.Specifications), JsonSerializer.Serialize(after.Specifications));
        Assert.Equal(before.CuttingTotal, after.CuttingTotal); Assert.Equal(before.Projects, after.Projects);
        Assert.Same(draft, f.Model.SelectedDraft); Assert.Same(row, f.Model.SelectedOrder);
        Assert.Same(projectRow, f.Model.SelectedProject); Assert.Same(savedCalculation, f.Model.SelectedDraft?.Saved);
        Assert.Equal("Ручная правка", draft.Comment); Assert.Equal("Не сохранено", f.Model.Blcb);
        Assert.Equal("Несохранённый менеджер", f.Model.ManagerName); Assert.True(f.Model.HasPendingEdits);
        Assert.Equal("Ручное письмо", f.Model.ResultText); Assert.False(f.Model.ResultIsStale);
        var unchanged = f.Model.CreateResponsibleChange()!;
        await f.Model.ChangeResponsibleAsync(unchanged, target.Id);
        Assert.Equal(after.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single().Version);
        await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var savedHeader = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single();
        Assert.Equal(target.Id, savedHeader.TechnologistId); Assert.Equal("Не сохранено", savedHeader.Blcb);
        Assert.Equal(f.Model.ManagerName, f.Model.Employees.Single(e => e.Id == savedHeader.ManagerId).Name); Assert.Same(draft, f.Model.SelectedDraft);
    }

    [Fact]
    public async Task ConcurrentChangeRejectsOldRequestAndPreservesLocalEdits()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var request = f.Model.CreateResponsibleChange()!;
        var before = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single();
        var actor = f.Model.CurrentEmployee!.Id;
        await f.Store.WriteOrderAsync(before.Id, before.Version, book => WorkflowChanges.UpdateOrder(book, before.Id,
            before.Version, before.ManagerId, before.TechnologistId, "Правка коллеги", actor, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        f.Model.Blcb = "Мой черновик"; f.Model.ResultText = "Моё письмо";
        await f.Model.ChangeResponsibleAsync(request, f.Model.Employees.Last().Id);
        Assert.NotNull(f.Model.Error); Assert.Equal(before.TechnologistId, f.Model.Technologist!.Id);
        var after = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single();
        Assert.Equal(before.Version + 1, after.Version); Assert.Equal("Правка коллеги", after.Blcb);
        Assert.Equal(before.TechnologistId, after.TechnologistId);
        Assert.Equal("Мой черновик", f.Model.Blcb); Assert.Equal("Моё письмо", f.Model.ResultText);
    }

    [Fact]
    public async Task RequestCannotBeAppliedToAnotherCard()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var request = f.Model.CreateResponsibleChange()!;
        f.Model.NewOrderCommand.Execute(null); f.Model.NewCustomer = "Другая заявка"; f.Model.NewManager = "Иванов";
        await f.Model.CreateOrderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var before = JsonSerializer.Serialize(await f.Store.ReadAsync(TestContext.Current.CancellationToken));
        await f.Model.ChangeResponsibleAsync(request, f.Model.Employees.Last().Id);
        Assert.NotNull(f.Model.Error);
        Assert.Equal(before, JsonSerializer.Serialize(await f.Store.ReadAsync(TestContext.Current.CancellationToken)));
    }
}
