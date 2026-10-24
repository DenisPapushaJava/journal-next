using System.Security.Cryptography;
using Jornal.Core.Pricing;
using Jornal.Desktop.ViewModels;
using Jornal.Infrastructure.Local;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class LegacyPricesTests
{
    private static readonly string CustomTable = "PriceMachine_" + new string('a', 32);
    private static PriceTable Table(string name, int? price) => new(name, true, [new(2.5m, "2,5", price), new(3, "3", 0), new(4, "4", null)]);
    private static FakePrices Source() => new(PriceTables.All.Select((name, index) => Table(name, 100 + index))
        .Append(Table(CustomTable, 250)).ToDictionary(t => t.Name));

    [Fact]
    public async Task UsedTablesKeepHansGroupsZeroAndCustomMachineWithoutReadingUnusedPrices()
    {
        var source = Source(); var catalog = await LegacyPriceCatalogReader.ReadAsync(source, source, TestContext.Current.CancellationToken);
        Assert.Equal(5, catalog.Machines.Length);
        Assert.Equal(1, source.Requests.Count(n => n == PriceTables.Laser));
        Assert.DoesNotContain("PriceLM500", source.Requests); Assert.DoesNotContain(PriceCatalog.UnusedHansLaser, source.Requests);
        Assert.Equal(101, catalog.Rates.Single(r => r.Key == new WorkflowRateKey("Лазер", "Базовая", 2.5m)).PricePerMeter);
        Assert.Equal(104, catalog.Rates.Single(r => r.Key == new WorkflowRateKey("HansLaser", "Нержавеющая сталь", 2.5m)).PricePerMeter);
        Assert.Equal(105, catalog.Rates.Single(r => r.Key == new WorkflowRateKey("HansLaser", "Цветные металлы", 2.5m)).PricePerMeter);
        Assert.Equal(250, catalog.Rates.Single(r => r.Key == new WorkflowRateKey("Гидроабразив", "Базовая", 2.5m)).PricePerMeter);
        Assert.Contains(catalog.Rates, r => r.Key.ThicknessMm == 3 && r.PricePerMeter == 0);
        Assert.DoesNotContain(catalog.Rates, r => r.Key.ThicknessMm == 4); Assert.NotEmpty(catalog.Warnings);
    }

    [Fact]
    public void ImportUpdatesMeterPricesPreservesPiercePricesAndHistoryAndRemovesUnavailableImportedRates()
    {
        var book = WorkflowWorkspace.Empty; var actor = book.Employees[0].Id;
        book = WorkflowChanges.CreateOrder(book, "ООО «Пример»", actor, actor, actor, DateTimeOffset.UtcNow, out var orderId);
        var rate = new CuttingRate(Guid.NewGuid(), "Лазер", "Базовая", 2.5m, 100, 7);
        book = WorkflowChanges.SetRate(book, rate);
        var removed = new CuttingRate(Guid.NewGuid(), "Лазер", "Базовая", 4, 150, 8);
        var local = new CuttingRate(Guid.NewGuid(), "Труборез", "Базовая", 1, 50, 0);
        book = WorkflowChanges.SetRate(WorkflowChanges.SetRate(book, removed), local);
        var parameters = new CuttingParameters("Лазер", "Ст3", "Базовая", 2.5m, "1500×3000", 1, 10, 2, null, null, 1.1m, true, null);
        book = WorkflowChanges.SaveSpecification(book, orderId, book.Orders[0].Version, Guid.NewGuid(), parameters,
            "1023-0930", new(2026, 10, 23), actor, DateTimeOffset.UtcNow);
        var originalOrder = book.Orders[0];
        book = book with { PriceSource = new("previous.mdb", DateTimeOffset.UtcNow, [LegacyPriceImport.Key(rate), LegacyPriceImport.Key(removed)]) };
        var catalog = new LegacyPriceCatalog([new("Лазер", CuttingMachine.VintechProfile)],
            [new(LegacyPriceImport.Key(rate), 200), new(new("Лазер", "Базовая", 6), 300)], []);
        var next = LegacyPriceImport.Apply(book, "source.mdb", catalog, DateTimeOffset.UtcNow);
        var updated = next.Rates.Single(r => r.ThicknessMm == 2.5m);
        Assert.Equal(200, updated.PricePerMeter); Assert.Equal(7, updated.PricePerPierce); Assert.NotEqual(rate.Id, updated.Id);
        Assert.Equal(1, next.Rates.Single(r => r.ThicknessMm == 6).PricePerPierce);
        Assert.DoesNotContain(next.Rates, r => r.Machine == "Лазер" && r.ThicknessMm == 4);
        Assert.Contains(next.Rates, r => r.Id == local.Id && r.PricePerMeter == local.PricePerMeter); Assert.Same(originalOrder, next.Orders[0]);
        Assert.Equal(1115.4m, next.Orders[0].CuttingTotal);
    }

    [Fact]
    public async Task FailedOrCancelledReadNeverEnablesImportOrAppliesPartialPrices()
    {
        var count = 0;
        var failing = new FailedSource();
        var vm = new WorkflowDatabaseViewModel(Path.Combine(Path.GetTempPath(), "source.mdb"), "", failing,
            (_, _, _) => { count++; return Task.FromResult<string?>(null); });
        await vm.ReadCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Error); Assert.Empty(vm.Prices); Assert.False(vm.ImportCommand.CanExecute(null)); Assert.Equal(0, count);
        Assert.Throws<WorkflowValidationException>(() => LegacyPriceCatalogReader.NormalizePath("source.mdb"));
        var cancelled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LegacyPriceCatalogReader.ReadAsync(Source(), Source(), cancelled));
        var negative = Source(); negative.Tables[PriceTables.Plasma] = Table(PriceTables.Plasma, -1);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => LegacyPriceCatalogReader.ReadAsync(negative, negative, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LocalSourceIsReadOnlyAndConnectionSurvivesRestartInOwnWorkspace()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Skip("Local legacy JSON is a Linux development fixture."); return; }
        var dir = Path.Combine(Path.GetTempPath(), "workflow-legacy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "source.journal-test.json"); var source = Source();
            await LocalJournalStore.InitializeAsync(path, [], source.Tables.Values.ToArray(), "fixture.mdb", new string('a', 64),
                TestContext.Current.CancellationToken, [source.Custom]);
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            var catalog = await new LegacyPriceCatalogReader().ReadAsync(path, TestContext.Current.CancellationToken);
            var store = new LocalWorkflowWorkspaceStore(Path.Combine(dir, "workflow.json"));
            await store.WriteAsync(0, w => LegacyPriceImport.Apply(w, path, catalog, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            var loaded = await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(path, loaded.PriceSource!.Path); Assert.Equal(catalog.Rates.Length, loaded.Rates.Length);
            Assert.All(loaded.Rates, r => Assert.Equal(1, r.PricePerPierce));
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
            Assert.Empty(loaded.Orders);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task CancelledImportDoesNotReportSuccessOrPersistConnection()
    {
        var dir = Path.Combine(Path.GetTempPath(), "workflow-cancel-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalWorkflowWorkspaceStore(Path.Combine(dir, "workspace.json"));
            var model = new WorkflowViewModel(store, new AppUpdatesViewModel(new WorkflowAppUpdater()));
            var source = Source(); var catalog = await LegacyPriceCatalogReader.ReadAsync(source, source, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.ApplyLegacyPricesAsync(Path.Combine(dir, "source.mdb"), catalog, new(true)));
            var workspace = await store.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Null(workspace.PriceSource); Assert.Empty(workspace.Rates); Assert.Equal(0, workspace.Version);
            model.Detach();
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task RealMdbCopyLoadsPricesWithoutChangingItsBytes()
    {
        var fixture = Environment.GetEnvironmentVariable("WORKFLOW_LEGACY_MDB");
        if (fixture is null) { Assert.Skip("Set WORKFLOW_LEGACY_MDB to a private Jet MDB fixture."); return; }
        var dir = Path.Combine(Path.GetTempPath(), "workflow-mdb-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "source.mdb"); File.Copy(fixture, path);
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            var catalog = await new LegacyPriceCatalogReader().ReadAsync(path, TestContext.Current.CancellationToken);
            Assert.NotEmpty(catalog.Rates); Assert.Contains(catalog.Machines, m => m.Name == "HansLaser");
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)));
        }
        finally { Directory.Delete(dir, true); }
    }

    private sealed class FailedSource : ILegacyPriceCatalogReader
    {
        public Task<LegacyPriceCatalog> ReadAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromException<LegacyPriceCatalog>(new PriceReadException("База данных недоступна."));
    }
    private sealed class FakePrices(Dictionary<string, PriceTable> tables) : IPriceReader, IMachineCatalogReader
    {
        public Dictionary<string, PriceTable> Tables { get; } = tables;
        public CuttingMachine Custom { get; } = new("Гидроабразив", CustomTable, CuttingMachine.WaterjetProfile);
        public List<string> Requests { get; } = [];
        public Task<PriceTable> ReadAsync(string tableName, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Requests.Add(tableName); return Task.FromResult(Tables[tableName]); }
        public Task<IReadOnlyList<CuttingMachine>> ReadMachinesAsync(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<CuttingMachine>>([Custom]); }
    }
}
