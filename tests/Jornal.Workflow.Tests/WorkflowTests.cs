using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowTests
{
    private static CuttingParameters Parameters(decimal coefficient = 1.1m, int? pierces = 100) =>
        new("ГАР", "09Г2С", "Базовая", 20, "Лист 20×1500×6000", 1, 80, pierces, 1413, 4320, coefficient, false, null);
    private static CuttingRate Rate(decimal? pierce = 5) => new(Guid.NewGuid(), "ГАР", "Базовая", 20, 230, pierce);
    [Fact]
    public void ManualCoefficientAppliesToMetersAndPiercesTogether()
    {
        var quote = CuttingCalculation.Calculate(Parameters(), Rate());
        Assert.Equal(18400, quote.LengthCost); Assert.Equal(500, quote.PierceCost); Assert.Equal(20790, quote.TotalCost);
        Assert.Equal("length-pierces-manual/v1-exact", quote.FormulaVersion);
    }
    [Fact]
    public void UnknownPiercesOrPriceCannotBecomeZeroAndMaterialMustBeReviewed()
    {
        Assert.Throws<WorkflowValidationException>(() => CuttingCalculation.Calculate(Parameters(pierces: null), Rate()));
        Assert.Throws<WorkflowValidationException>(() => CuttingCalculation.Calculate(Parameters(), Rate(null)));
        Assert.Throws<WorkflowValidationException>(() => CuttingCalculation.Calculate(Parameters() with { MaterialNeedsReview = true }, Rate()));
        Assert.Equal(20240, CuttingCalculation.Calculate(Parameters(pierces: 0), Rate(0)).TotalCost);
    }
    [Fact]
    public void RatesMustMatchTheWholePricingKeyAndOverflowIsFriendly()
    {
        Assert.Throws<WorkflowValidationException>(() => CuttingCalculation.Calculate(Parameters(), Rate() with { MaterialGroup = "Нержавеющая сталь" }));
        Assert.Throws<WorkflowValidationException>(() => CuttingCalculation.Calculate(Parameters() with { CutLengthMeters = decimal.MaxValue }, Rate()));
    }
    [Fact]
    public void IdenticalTariffIsNotAChange()
    {
        var rate = Rate(); var workspace = WorkflowChanges.SetRate(WorkflowWorkspace.Empty, rate);
        Assert.Same(workspace, WorkflowChanges.SetRate(workspace, rate with { Id = Guid.NewGuid() }));
    }
    [Fact]
    public void ClearingInputKeepsDraftInvalidWithoutThrowing()
    {
        var draft = new SpecificationDraft { Machine = null!, Material = null!, Thickness = null!, Length = null! };
        Assert.False(draft.CanSave);
        Assert.NotNull(draft.CalculationError);
        Assert.Throws<WorkflowValidationException>(() => draft.Parameters());
    }
    [Fact]
    public void UpdateRestartPreservesEditableResultInOwnProfileAndConsumesItOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "workflow-restart-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "restart.json");
            var store = new WorkflowRestartStore(path);
            var draft = new WorkflowRestartDraft(1, Guid.NewGuid(), "Исправленный вручную результат\n");
            store.Save(draft);
            Assert.Equal(draft, new WorkflowRestartStore(path).Take());
            Assert.Null(store.Take());
            File.WriteAllText(path, "{\"Schema\":2,\"ResultText\":\"text\"}");
            Assert.Throws<InvalidDataException>(() => store.Take());
            Assert.True(File.Exists(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public async Task VersionsPricesAndAuthorsSurviveTransferAndRestartWithoutNoOpWrites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "workflow-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "workspace.json"); var store = new LocalWorkflowWorkspaceStore(path);
            var initial = await store.ReadAsync(TestContext.Current.CancellationToken);
            var actor = initial.Employees[1].Id; var nextActor = initial.Employees[2].Id; Guid orderId = default;
            var book = await store.WriteAsync(0, w => WorkflowChanges.CreateOrder(w, "ООО «Пример»", w.Employees[0].Id, actor, actor,
                DateTimeOffset.UtcNow, out orderId), TestContext.Current.CancellationToken);
            var rate = Rate(); book = await store.WriteAsync(book.Version, w => WorkflowChanges.SetRate(w, rate), TestContext.Current.CancellationToken);
            var specId = Guid.NewGuid();
            book = await store.WriteAsync(book.Version, w => WorkflowChanges.SaveSpecification(w, orderId, w.Orders[0].Version, specId,
                Parameters(), "1023-0920", new(2026, 10, 23), actor, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            var version = book.Version; var original = book.Orders[0].Specifications[0].Current;
            book = await store.WriteAsync(book.Version, w => WorkflowChanges.SaveSpecification(w, orderId, w.Orders[0].Version, specId,
                Parameters(), "1023-0920", new(2026, 10, 23), actor, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            Assert.Equal(version, book.Version); Assert.Single(book.Orders[0].Specifications[0].Revisions);
            book = await store.WriteAsync(book.Version, w => WorkflowChanges.UpdateOrder(w, orderId, w.Orders[0].Version,
                w.Employees[0].Id, nextActor, "0005", 2026, actor, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            book = await store.WriteAsync(book.Version, w => WorkflowChanges.SetRate(w, rate with { Id = Guid.NewGuid(), PricePerMeter = 240 }), TestContext.Current.CancellationToken);
            book = await store.WriteAsync(book.Version, w => WorkflowChanges.SaveSpecification(w, orderId, w.Orders[0].Version, specId,
                Parameters() with { HasBending = true }, "1023-0920", new(2026, 10, 23), nextActor, DateTimeOffset.UtcNow,
                quotedRate: rate), TestContext.Current.CancellationToken);
            var loaded = await new LocalWorkflowWorkspaceStore(path).ReadAsync(TestContext.Current.CancellationToken);
            var order = Assert.Single(loaded.Orders); Assert.Equal("0005", order.Blcb); Assert.Equal(nextActor, order.TechnologistId);
            var revisions = Assert.Single(order.Specifications).Revisions; Assert.Equal(2, revisions.Length);
            Assert.Equal(original, revisions[0]); Assert.Equal(actor, revisions[0].AuthorId); Assert.Equal(nextActor, revisions[1].AuthorId);
            Assert.Equal(230, revisions[1].Calculation.PricePerMeter); Assert.True(revisions[1].Parameters.HasBending);
            await Assert.ThrowsAsync<WorkflowConflictException>(() => store.WriteAsync(version, w => w, TestContext.Current.CancellationToken));
            Assert.Equal(loaded.Version, (await store.ReadAsync(TestContext.Current.CancellationToken)).Version);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public void SharedJsonStorageAndLegacyUpdateRepositoryAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new LocalWorkflowWorkspaceStore(@"\\server\share\workspace.json"));
        Assert.Throws<ArgumentException>(() => WorkflowIdentity.ValidateRepository(new(Jornal.Desktop.Updates.VelopackAppUpdater.RepositoryUrl)));
        Assert.StartsWith("workflow-", WorkflowIdentity.Channel);
        Assert.NotEqual("Journal", WorkflowIdentity.SettingsFolder);
        Assert.NotEqual(Jornal.Desktop.Customization.JsonUiPreferencesStore.DefaultPath, WorkflowIdentity.PreferencesPath);
        Assert.NotEqual(Jornal.Desktop.Updates.JsonAppUpdatePreferencesStore.DefaultPath, WorkflowIdentity.UpdatePreferencesPath);
        Assert.Equal("DenisPapusha.JournalWorkflow", WorkflowIdentity.PackageId);
        Assert.Equal("workflow-v", WorkflowIdentity.TagPrefix);
        Assert.StartsWith(WorkflowIdentity.ProfileDirectory + Path.DirectorySeparatorChar, WorkflowIdentity.UpdatePreferencesPath);
        var updater = new WorkflowAppUpdater(); Assert.False(updater.IsInstalled); Assert.Equal("https://github.com/DenisPapushaJava/journal-next", WorkflowIdentity.UpdatesRepository!.AbsoluteUri);
    }
    [Fact]
    public void ReleasesPageUsesValidatedWorkflowRepositoryAndNormalizesGitSuffix()
    {
        Assert.Equal("https://github.com/example/workflow-releases/releases", WorkflowIdentity.ReleasesPageFor(new("https://github.com/example/workflow-releases.git/")).AbsoluteUri);
        Assert.Throws<ArgumentException>(() => WorkflowIdentity.ReleasesPageFor(new("https://github.com/DenisPapushaJava/journal-v2.0.git/")));
        Assert.Throws<ArgumentException>(() => WorkflowIdentity.ReleasesPageFor(new("https://github.com/DenisPapushaJava/journal-v2.0/releases")));
        Assert.Throws<ArgumentException>(() => WorkflowIdentity.ReleasesPageFor(new("https://github.com/example/workflow-releases?target=legacy")));
    }
    [Fact]
    public void ImportedParametersDoNotRunTheOldCoefficientFormulaAndAwjcNeedsConfirmation()
    {
        var report = new Jornal.Parsing.Specifications.SpecificationParseResult(Jornal.Parsing.Specifications.WaterjetPdfSpecificationParser.Profile,
            null, null, new("Сталь 20/09Г2С", []), null, null, null, null, new(3, []), new(1000, []), null, []) { IsRecognized = true };
        var draft = WorkflowViewModel.FromReport(report, Path.Combine(Path.GetTempPath(), "sample.pdf"));
        Assert.Equal("1", draft.Coefficient); Assert.Equal("1000", draft.Pierces); Assert.True(draft.MaterialNeedsReview); Assert.False(draft.MaterialConfirmed);
        var mapped = WorkflowViewModel.FromReport(report, Path.Combine(Path.GetTempPath(), "sample.pdf"),
            [new("Гидроабразив", Jornal.Core.Pricing.CuttingMachine.WaterjetProfile)]);
        Assert.Equal("Гидроабразив", mapped.Machine); Assert.True(mapped.MaterialNeedsReview);
        var renamed = WorkflowViewModel.FromReport(report, Path.Combine(Path.GetTempPath(), "sample.pdf"),
            [new("Гидроабразив новый", Jornal.Core.Pricing.CuttingMachine.WaterjetProfile) { LegacyName = "ГАР" },
             new("Запасной ГАР", Jornal.Core.Pricing.CuttingMachine.WaterjetProfile)]);
        Assert.Equal("Гидроабразив новый", renamed.Machine); Assert.True(renamed.MaterialNeedsReview);
        var ambiguous = WorkflowViewModel.FromReport(report, Path.Combine(Path.GetTempPath(), "sample.pdf"),
            [new("Гидроабразив 1", Jornal.Core.Pricing.CuttingMachine.WaterjetProfile), new("Гидроабразив 2", Jornal.Core.Pricing.CuttingMachine.WaterjetProfile)]);
        Assert.Empty(ambiguous.Machine); Assert.Contains("нескольким станкам", ambiguous.ImportNotice);
        draft.Material = "09Г2С"; Assert.False(draft.MaterialNeedsReview);
    }
}
