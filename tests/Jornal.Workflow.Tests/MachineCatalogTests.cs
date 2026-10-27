using System.Text.Json;
using System.Text.Json.Nodes;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class MachineCatalogTests
{
    private static LegacyPriceCatalog Source(decimal price = 200) => new(
        [new("Лазер", "block-html/v1") { LegacyKey = "legacy:Лазер" }], [new(new("Лазер", "Базовая", 3), price)], []);

    [Fact]
    public void RenameUsesPermanentIdAndDoesNotRewriteHistoryOrBreakLegacyReload()
    {
        var book = LegacyPriceImport.Apply(WorkflowTestData.CreateWorkspace(), "source.mdb", Source(), DateTimeOffset.UtcNow);
        var laser = book.Machines.Single(m => m.Name == "Лазер"); var actor = book.Employees[0].Id;
        book = WorkflowChanges.CreateOrder(book, "ООО «Пример»", actor, actor, actor, DateTimeOffset.UtcNow, out var orderId);
        var parameters = new CuttingParameters("Лазер", "Ст3", "Базовая", 3, "1500×3000", 1, 10, 5, null, null, 1, false, null) { MachineId = laser.Id };
        var specId = Guid.NewGuid();
        book = WorkflowChanges.SaveSpecification(book, orderId, 1, specId, parameters, "1023-1100", new(2026, 10, 23), actor, DateTimeOffset.UtcNow);
        var original = book.Orders[0].Specifications[0].Current; var rateId = book.Rates[0].Id;
        var renamed = WorkflowChanges.UpdateCatalog(book, book.Machines.Select(m => m.Id == laser.Id ? m with { Name = "Лазер 6 кВт" } : m).ToArray(), book.Rates);
        Assert.Equal(laser.Id, renamed.Machines.Single(m => m.Name == "Лазер 6 кВт").Id);
        Assert.Equal(rateId, renamed.Rates[0].Id); Assert.Equal(laser.Id, renamed.Rates[0].MachineId);
        Assert.Same(original, renamed.Orders[0].Specifications[0].Current); Assert.Equal("Лазер", original.Parameters.Machine);
        var noChange = WorkflowChanges.SaveSpecification(renamed, orderId, renamed.Orders[0].Version, specId,
            parameters with { Machine = "Лазер 6 кВт" }, "1023-1100", new(2026, 10, 23), actor, DateTimeOffset.UtcNow);
        Assert.Same(renamed, noChange);
        var refreshed = LegacyPriceImport.Apply(renamed, "source.mdb", Source(350), DateTimeOffset.UtcNow);
        Assert.Equal(renamed.Machines.Length, refreshed.Machines.Length);
        Assert.Equal("Лазер 6 кВт", refreshed.Rates[0].Machine); Assert.Equal(laser.Id, refreshed.Rates[0].MachineId);
        Assert.Equal(350, refreshed.Rates[0].PricePerMeter); Assert.Equal(1, refreshed.Rates[0].PricePerPierce);
        Assert.Equal(2005, refreshed.Orders[0].CuttingTotal);
    }

    [Fact]
    public void CustomMachineKeepsIdentityWhenLegacyAndLocalNamesBothChange()
    {
        var source = new LegacyPriceCatalog([new("Гидроабразив", "waterjet-awjc/v1") { LegacyKey = "table:fixed-id" }],
            [new(new("Гидроабразив", "Базовая", 10), 300)], []);
        var book = LegacyPriceImport.Apply(WorkflowTestData.CreateWorkspace(), "source.mdb", source, DateTimeOffset.UtcNow);
        var machine = book.Machines.Single(m => m.LegacyKey == "table:fixed-id");
        book = WorkflowChanges.UpdateCatalog(book, book.Machines.Select(m => m.Id == machine.Id ? m with { Name = "ГАР № 2", ImportProfile = null } : m).ToArray(), book.Rates);
        source = source with { Machines = [source.Machines[0] with { Name = "Новый ГАР" }], Rates = [new(new("Новый ГАР", "Базовая", 10), 400)] };
        var next = LegacyPriceImport.Apply(book, "source.mdb", source, DateTimeOffset.UtcNow);
        Assert.Equal(book.Machines.Length, next.Machines.Length);
        Assert.Equal(machine.Id, next.Rates[0].MachineId); Assert.Equal("ГАР № 2", next.Rates[0].Machine);
        Assert.Null(next.Machines.Single(m => m.Id == machine.Id).ImportProfile);
        Assert.Equal("Новый ГАР", next.Machines.Single(m => m.Id == machine.Id).LegacyName);
    }

    [Fact]
    public void DuplicateNamesThicknessesAndDanglingRatesAreRejected()
    {
        var book = LegacyPriceImport.Apply(WorkflowTestData.CreateWorkspace(), "source.mdb", Source(), DateTimeOffset.UtcNow);
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.UpdateCatalog(book,
            book.Machines.Select(m => m with { Name = "Одно имя" }).ToArray(), book.Rates));
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.UpdateCatalog(book, book.Machines,
            [.. book.Rates, book.Rates[0] with { Id = Guid.NewGuid() }]));
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.UpdateCatalog(book, book.Machines.Skip(1).ToArray(), book.Rates));
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.UpdateCatalog(book, book.Machines,
            [book.Rates[0] with { PricePerPierce = null }]));
        Assert.Same(book, WorkflowChanges.UpdateCatalog(book, book.Machines, book.Rates));
    }

    [Fact]
    public async Task SchemaOneMigrationBacksUpOnceAndAssignsStableIdsToAllReferences()
    {
        var dir = Path.Combine(Path.GetTempPath(), "workflow-schema1-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var book = LegacyPriceImport.Apply(WorkflowTestData.CreateWorkspace(), "source.mdb", Source(), DateTimeOffset.UtcNow);
            var actor = book.Employees[0].Id;
            book = WorkflowChanges.CreateOrder(book, "ООО «Пример»", actor, actor, actor, DateTimeOffset.UtcNow, out var orderId);
            book = WorkflowChanges.SaveSpecification(book, orderId, 1, Guid.NewGuid(),
                new("Лазер", "Ст3", "Базовая", 3, "1500×3000", 1, 10, 5, null, null, 1, false, null),
                "1023-1100", new(2026, 10, 23), actor, DateTimeOffset.UtcNow);
            var node = WorkflowTestData.LegacyJson(book, 1);
            foreach (var machine in node["Machines"]!.AsArray()) { machine!.AsObject().Remove("Id"); machine.AsObject().Remove("LegacyKey"); machine.AsObject().Remove("LegacyName"); }
            foreach (var rate in node["Rates"]!.AsArray()) rate!.AsObject().Remove("MachineId");
            foreach (var order in node["Orders"]!.AsArray()) foreach (var spec in order!["Specifications"]!.AsArray())
                foreach (var revision in spec!["Revisions"]!.AsArray()) revision!["Parameters"]!.AsObject().Remove("MachineId");
            var path = Path.Combine(dir, "workspace.json"); var bytes = System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            var store = new LocalWorkflowWorkspaceStore(path); var migrated = await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(WorkflowWorkspace.CurrentSchema, migrated.SchemaVersion); Assert.Equal(book.Orders[0].CuttingTotal, migrated.Orders[0].CuttingTotal);
            var id = migrated.Machines.Single(m => m.Name == "Лазер").Id; Assert.NotEqual(Guid.Empty, id);
            Assert.Equal(id, migrated.Rates[0].MachineId); Assert.Equal(id, migrated.Orders[0].Specifications[0].Current.Parameters.MachineId);
            var backup = Assert.Single(Directory.GetFiles(dir, "*.bak")); Assert.Equal(bytes, await File.ReadAllBytesAsync(backup, TestContext.Current.CancellationToken));
            var second = await store.ReadAsync(TestContext.Current.CancellationToken); Assert.Equal(id, second.Rates[0].MachineId);
            Assert.Single(Directory.GetFiles(dir, "*.bak"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
