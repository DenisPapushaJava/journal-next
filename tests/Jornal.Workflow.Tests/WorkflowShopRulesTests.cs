using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using PdfSharp.Drawing;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowShopRulesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static void Program(MapSigningFixture f, string name = "Лист1.cnc") => File.WriteAllText(Path.Combine(f.Root, name), "UP");
    private static async Task<ShopProjectFiles> Plan(MapSigningFixture f) => Assert.Single(await WorkflowShopDiscovery.DiscoverAsync(f.Root + "-shop", f.Order, f.Book, Token));
    private static Task<ShopTransferResult[]> Copy(MapSigningFixture f, ShopProjectFiles plan, bool reviewed = false) =>
        WorkflowShopTransfer.CopyAsync(f.Root + "-shop", "test", f.Order.Id, [new(plan, plan.Files.Where(file => file.Suggested).ToArray(), reviewed, null)], false, _ => Task.CompletedTask, token: Token);
    private static void Clean(MapSigningFixture f) { if (Directory.Exists(f.Root + "-shop")) Directory.Delete(f.Root + "-shop", true); }

    [Theory]
    [InlineData("Лазер", "HYPERTHERM")]
    [InlineData("Плазма", "Кристалл 260 + ГАЗ")]
    [InlineData("Газо-кислородная", "Произвольное имя")]
    public async Task MachineIdentityProjectAndUpReferencesAllowAutomaticTransferWithContinuationPages(string machine, string station)
    {
        using var f = new MapSigningFixture(machine);
        var extension = machine == "Лазер" ? ".cnc" : ".nc";
        Program(f, "Лист1" + extension); Program(f, "Лист2" + extension);
        f.Create(maps: 2, continuation: true, station: station);
        try
        {
            var model = new WorkflowShopTransferViewModel(f.Root + "-shop", "test", f.Order, f.Book, _ => Task.CompletedTask, _ => Task.CompletedTask);
            await model.RefreshAsync(); Assert.True(model.CanStartAutomatically, model.Projects.FirstOrDefault()?.SelectionHelp ?? model.Error);
            await model.CopyAsync(); Assert.True(model.TextCopied, model.Error);
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(Path.Combine(model.Projects[0].Destination, "Карта.pdf"));
            Assert.Equal(3, pdf.NumberOfPages); Assert.All(pdf.GetPages().Take(2), page => Assert.Contains("БЛЦБ-009337", page.Text));
        }
        finally { Clean(f); }
    }

    [Fact]
    public async Task QuotedUpReferenceAndEmptyFreeStationNameUseTheSameDiscoveryAndSigningRules()
    {
        using var f = new MapSigningFixture(); Program(f); f.Create(station: "", cnc: "\"Лист1.cnc\"");
        try
        {
            var model = new WorkflowShopTransferViewModel(f.Root + "-shop", "test", f.Order, f.Book, _ => Task.CompletedTask, _ => Task.CompletedTask);
            await model.RefreshAsync(); Assert.True(model.CanStartAutomatically, model.Projects.FirstOrDefault()?.SelectionHelp ?? model.Error);
            await model.CopyAsync(); Assert.True(model.TextCopied, model.Error);
        }
        finally { Clean(f); }
    }

    [Fact]
    public async Task ChangedForeignMapIsReclassifiedWithoutBlockingOrCopyingIt()
    {
        using var f = new MapSigningFixture(); Program(f); f.Create();
        f.Create("Чужая.pdf", native: "другой.rcam"); var plan = await Plan(f); Assert.False(plan.NeedsReview);
        f.Create("Чужая.pdf", native: "другой.rcam", comment: "Изменён посторонний проект");
        try
        {
            var result = Assert.Single(await Copy(f, plan)); Assert.True(result.Success, result.Message);
            Assert.False(File.Exists(Path.Combine(plan.Destination, "Чужая.pdf")));
        }
        finally { Clean(f); }
    }

    [Fact]
    public async Task ChangedForeignMapThatBecomesCompetingCurrentMapStopsWithItsName()
    {
        using var f = new MapSigningFixture(); Program(f); f.Create();
        f.Create("Чужая.pdf", native: "другой.rcam"); var plan = await Plan(f);
        f.Create("Чужая.pdf", comment: "Другой вариант той же карты");
        var result = Assert.Single(await Copy(f, plan)); Assert.False(result.Success);
        Assert.Contains("Чужая.pdf", result.Message); Assert.Contains("одну УП", result.Message);
        Assert.False(Directory.Exists(f.Root + "-shop"));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("deleted")]
    [InlineData("locked")]
    public async Task ExplicitlyExcludedFilesMayChangeBeDeletedOrBecomeLockedWithoutAffectingChosenBytes(string change)
    {
        using var f = new MapSigningFixture(); Program(f); f.Create();
        var extra = f.Create("Чужая.pdf", native: "другой.rcam"); var plan = await Plan(f);
        if (change == "changed") f.Create("Чужая.pdf", comment: "Теперь карта этого проекта, но явно исключена");
        if (change == "deleted") File.Delete(extra);
        using var locked = change == "locked" ? File.Open(extra, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        try
        {
            var result = Assert.Single(await Copy(f, plan, reviewed: true)); Assert.True(result.Success, result.Message);
            Assert.False(File.Exists(Path.Combine(plan.Destination, "Чужая.pdf")));
        }
        finally { Clean(f); }
    }

    [Fact]
    public async Task NewDrawingPdfDoesNotInvalidateSelectionButNewCurrentMapDoes()
    {
        using var f = new MapSigningFixture(); Program(f); f.Create(); var plan = await Plan(f);
        var drawing = Path.Combine(f.Root, "Чертёж.pdf");
        using (var pdf = new PdfSharp.Pdf.PdfDocument())
        {
            using (var g = XGraphics.FromPdfPage(pdf.AddPage())) g.DrawString("Чертёж детали", WorkflowPdfFont.Create(10), XBrushes.Black, new XPoint(50, 50));
            pdf.Save(drawing);
        }
        Assert.Empty(await WorkflowShopTransfer.PreflightAsync(f.Root + "-shop", "test", f.Order.Id,
            [new(plan, plan.Files.Where(file => file.Suggested).ToArray(), false, null)], false, _ => Task.CompletedTask, token: Token));
        f.Create("Изменённая карта.pdf", comment: "Актуальная карта?");
        var result = Assert.Single(await Copy(f, plan)); Assert.False(result.Success);
        Assert.Contains("Изменённая карта.pdf", result.Message); Assert.False(Directory.Exists(f.Root + "-shop"));
    }

    [Fact]
    public async Task MissingReferencedUpAndUnreadableSelectedFileExplainExactFileAndProject()
    {
        using var f = new MapSigningFixture(); var map = f.Create(); var plan = await Plan(f);
        Assert.True(plan.NeedsReview); Assert.Contains("Лист1.cnc", plan.ReviewReason); Assert.Contains("Карта.pdf", plan.ReviewReason);
        Program(f); plan = await Plan(f);
        using var locked = File.Open(map, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = Assert.Single(await Copy(f, plan)); Assert.False(result.Success);
        Assert.Contains("Карта.pdf", result.Message); Assert.Contains(f.Project.Name, result.Message); Assert.Contains("чтения", result.Message);
    }
}
