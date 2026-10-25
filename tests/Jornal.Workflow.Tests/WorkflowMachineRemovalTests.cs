using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowMachineRemovalTests
{
    [Fact]
    public async Task UnusedSavedMachineAndItsRatesCanBeRemovedAndAnUnsavedMachineCanBeDiscarded()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var editor = f.Model.CreateCatalogSettings(); editor.AddMachineCommand.Execute(null);
        editor.RemoveMachineCommand.Execute(null); Assert.False(editor.HasChanges);
        Assert.Equal(before.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
        editor.AddMachineCommand.Execute(null); var id = editor.SelectedMachine!.Id;
        editor.SelectedMachine.Name = "Случайный станок"; editor.AddRateCommand.Execute(null);
        editor.SelectedRate!.Thickness = "4"; editor.SelectedRate.PricePerMeter = "100";
        await editor.SaveCommand.ExecuteAsync(null); Assert.Null(editor.Error);
        editor.SelectedMachine = editor.Machines.Single(m => m.Id == id);
        Assert.True(editor.RemoveMachineCommand.CanExecute(null)); editor.RemoveMachineCommand.Execute(null);
        Assert.True(editor.HasChanges); await editor.SaveCommand.ExecuteAsync(null); Assert.Null(editor.Error);
        var after = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(after.Machines, m => m.Id == id); Assert.DoesNotContain(after.Rates, r => r.MachineId == id);
        Assert.Equal(before.Orders.Single().Version, after.Orders.Single().Version);
    }

    [Fact]
    public async Task AProjectOrCurrentCalculationProtectsItsMachineAndReplacingCalculationReleasesThePreviousMachine()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var editor = f.Model.CreateCatalogSettings(); editor.SelectedMachine = editor.Machines.Single(m => m.Id == project.MachineId);
        Assert.False(editor.RemoveMachineCommand.CanExecute(null)); Assert.Contains("Есть проекты", editor.RemovalReason);
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.UpdateCatalog(book,
            book.Machines.Where(m => m.Id != project.MachineId).ToArray(), book.Rates.Where(r => r.MachineId != project.MachineId).ToArray()));
        var gas = book.Machines.Single(m => m.Name == "Газо-кислородная"); var plasma = book.Machines.Single(m => m.Name == "Плазма");
        var order = book.Orders.Single(); var actor = book.Employees[1].Id; var specId = Guid.NewGuid();
        var parameters = new CuttingParameters(gas.Name, "Ст3", "Базовая", 6, "Лист", 1, 1, 1, null, 60, 1, false, null) { MachineId = gas.Id };
        book = await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.SaveSpecification(w, order.Id, order.Version,
            specId, parameters, "Тест", new(2026, 10, 24), actor, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Contains("сохранённые расчёты", WorkflowChanges.MachineRemovalReason(book, gas.Id));
        order = book.Orders.Single(); var version = order.Version;
        book = await f.Store.WriteOrderAsync(order.Id, version, w => WorkflowChanges.SaveSpecification(w, order.Id, version,
            specId, parameters with { Machine = plasma.Name, MachineId = plasma.Id }, "Тест", new(2026, 10, 24), actor, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        Assert.Null(WorkflowChanges.MachineRemovalReason(book, gas.Id));
        Assert.Contains("сохранённые расчёты", WorkflowChanges.MachineRemovalReason(book, plasma.Id));
        var removed = WorkflowChanges.UpdateCatalog(book,
            book.Machines.Where(m => m.Id != gas.Id).ToArray(), book.Rates.Where(r => r.MachineId != gas.Id).ToArray());
        removed.Validate(); Assert.DoesNotContain(removed.Machines, m => m.Id == gas.Id);
    }

    [Fact]
    public async Task InactiveDraftProtectsItsMachineAndCancellingTheDraftReleasesThatProtection()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var orderId = f.Model.SelectedOrder!.Order.Id; var machine = f.Model.ProjectMachines.Single(m => m.Name == "Труборез");
        f.Model.AddLegacySpecificationDraft(); f.Model.SelectedDraft!.UseMachine(machine);
        var editor = f.Model.CreateCatalogSettings(); editor.SelectedMachine = editor.Machines.Single(m => m.Id == machine.Id);
        Assert.False(editor.RemoveMachineCommand.CanExecute(null)); Assert.Contains("черновике", editor.RemovalReason);
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Иванов"; f.Model.NewCustomer = "Другая заявка"; await f.Model.CreateOrderCommand.ExecuteAsync(null);
        editor = f.Model.CreateCatalogSettings(); editor.SelectedMachine = editor.Machines.Single(m => m.Id == machine.Id);
        Assert.False(editor.RemoveMachineCommand.CanExecute(null));
        f.Model.SelectedOrder = f.Model.Orders.Single(o => o.Order.Id == orderId);
        f.Model.CancelDraftCommand.Execute(null);
        editor = f.Model.CreateCatalogSettings(); editor.SelectedMachine = editor.Machines.Single(m => m.Id == machine.Id);
        Assert.True(editor.RemoveMachineCommand.CanExecute(null));
    }

    [Fact]
    public async Task AnotherClientsNewProjectRejectsAStagedRemovalWithoutDiscardingTheEditor()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var editor = f.Model.CreateCatalogSettings(); var id = editor.Machines.Single(m => m.Name == "Труборез").Id;
        editor.SelectedMachine = editor.Machines.Single(m => m.Id == id); editor.RemoveMachineCommand.Execute(null);
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = book.Orders.Single();
        var project = await WorkflowProjectFiles.PrepareAsync(book.ProjectRoot!, order, "Пример", book.Machines.Single(m => m.Id == id),
            6, null, DateTimeOffset.Now, TestContext.Current.CancellationToken);
        await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowProjects.Add(w, order.Id, order.Version, project,
            book.Employees[1].Id, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await editor.SaveCommand.ExecuteAsync(null); Assert.NotNull(editor.Error); Assert.True(editor.HasChanges);
        Assert.DoesNotContain(editor.Machines, m => m.Id == id);
        var after = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Contains(after.Machines, m => m.Id == id); Assert.Contains(after.Orders.Single().Projects, p => p.Id == project.Id);
    }
}
