using Avalonia.Headless.XUnit;
using Jornal.Parsing.Documents;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowWaterjetShopTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset At = new(2026, 10, 27, 10, 0, 0, TimeSpan.FromHours(3));
    private static readonly CuttingProject Project = new(Guid.NewGuid(), Guid.NewGuid(), 1, WorkflowMachine.Defaults().Single(m => m.Name == "ГАР").Id,
        6, "job", Path.Combine(Path.GetTempPath(), "job.dig"), "waterjet-awjc/v1", At);
    private static ShopFile Up(string name = "sheet.NC") => new(name, name, 10, "up-" + name, ShopFileKind.Program, false, "manual");
    private static ShopFile Map(string name = "map.pdf", string? hash = null) => new(name, name, 10, hash ?? "map-" + name, ShopFileKind.Map, false, "manual");
    private static PdfPage Page(params (string Label, string Value)[] rows)
    {
        var words = new List<PdfWord>(); var y = 760;
        foreach (var (label, value) in rows)
        {
            words.Add(new(label, 25, y, 100, y + 9));
            if (value.Length > 0) words.Add(new(value, 125, y, 390, y + 9));
            y -= 20;
        }
        return new(1, 612, 792, words);
    }
    private static SourceDocument Pp(string program = "sheet.NC", string drawing = "job.dig", params PdfPage[] extra) => new(DocumentKind.Pdf, null, [],
        [Page(("Files", ""), ("CNC file", program), ("Drawing", drawing), ("Sheet size", "1000 x 2000 mm"), ("Tool setup", "")), ..extra]);
    private static SourceDocument Ce(string drawing = "job.dig", bool russian = false) => new(DocumentKind.Pdf, null, [],
        [Page((russian ? "Имя файла" : "Drawing file", drawing), (russian ? "Длинна реза" : "Cutting length", "1000 mm"),
            (russian ? "Толщина" : "Thickness", "6 mm"))]);
    private static ShopFile[] Select(ShopFile[] files, params (ShopFile File, SourceDocument Document)[] maps) => WorkflowWaterjetShopMaps.Select(Project, files,
        maps.Select(pair => WorkflowWaterjetShopMaps.Analyze(pair.File, pair.Document, Token)).ToArray());

    [Fact]
    public void PostprocessorLinksFullNamesAndIgnoresContinuationPagesAndAuthorColumn()
    {
        var up = Up("a long name 123.NC"); var map = Map("arbitrary.pdf");
        var page = Page(("Files", ""), ("CNC file", "a long name 123.NC"), ("Drawing", "job.dig"), ("Sheet size", "1000 x 2000"), ("Tool setup", ""));
        page = page with { Words = [..page.Words, new("author", 500, 740, 550, 749)] };
        var document = new SourceDocument(DocumentKind.Pdf, null, [], [page, Page(("Quantity", "2"), ("Quality", "Medium"))]);
        var evidence = WorkflowWaterjetShopMaps.Analyze(map, document, Token);
        Assert.Equal(WorkflowWaterjetShopMaps.Kind.Postprocessor, evidence.Type); Assert.Equal([up.Name], evidence.Programs); Assert.False(evidence.InvalidFields);
        Assert.All(Select([up, map], (map, document)), f => Assert.True(f.Suggested));
    }

    [Fact]
    public void RenamedExternalPairCanResolveStaleMissingCncReference()
    {
        var up = Up("List_3_8mm AMg3.NC"); var map = Map("List_3_8mm  AMg3.pdf");
        Assert.All(Select([up, map], (map, Pp("old internal name.NC"))), f => Assert.True(f.Suggested));
        var mismatched = Select([Up("unknown.NC"), map], (map, Pp("old internal name.NC")));
        Assert.All(mismatched, f => Assert.False(f.Suggested));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MetadataBelowLayoutPictureIsReadInLowerHalfOfPage(bool calculation)
    {
        var map = Map(calculation ? "sheet_ce.pdf" : "any.pdf"); var document = calculation ? Ce() : Pp();
        document = document with { Pages = document.Pages.Select(p => p with
            { Words = p.Words.Select(w => w with { Bottom = w.Bottom - 420, Top = w.Top - 420 }).ToArray() }).ToArray() };
        Assert.All(Select([Up(), map], (map, document)), f => Assert.True(f.Suggested));
    }

    [Fact]
    public void ExistingHeaderTargetAndDifferentNameTargetRequireReview()
    {
        var original = Up("original.NC"); var newer = Up("new.NC"); var map = Map("new_pp.pdf");
        var files = Select([original, newer, map], (map, Pp("original.NC")));
        Assert.All(files, f => Assert.False(f.Suggested)); Assert.True(new ShopProjectFiles(Project, "target", files, null, null).NeedsReview);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void CeLayoutCanBeTheOnlyMapWhenProjectAndExternalPairAgree(bool russian)
    {
        var up = Up("job.NC"); var map = Map("job_ce.pdf");
        var files = Select([up, map], (map, Ce(russian: russian))); Assert.All(files, f => Assert.True(f.Suggested));
        Assert.All(Select([Up("renamed-without-link.NC"), map], (map, Ce(russian: russian))), f => Assert.False(f.Suggested));
    }

    [Fact]
    public void CompletePpSetMakesSameProjectCalculationReportOptionalEvenWithDifferentFileName()
    {
        var up = Up(); var pp = Map("for-shop.pdf"); var ce = Map("calculation.pdf");
        var files = Select([up, pp, ce], (pp, Pp()), (ce, Ce()));
        Assert.True(files.Single(f => f.Name == ce.Name).Ignored); Assert.False(files.Single(f => f.Name == ce.Name).Suggested);
        Assert.False(new ShopProjectFiles(Project, "target", files, null, null).NeedsReview);
        Assert.All(files.Where(f => f.Name != ce.Name), f => Assert.True(f.Suggested));
    }

    [Fact]
    public void UnmappedProgramOrDifferentMapVersionsStillRequireReview()
    {
        var up = Up(); var extra = Up("new.NC"); var a = Map("a.pdf"); var b = Map("b.pdf");
        var extraFiles = Select([up, extra, a], (a, Pp()));
        Assert.False(extraFiles.Single(f => f.Name == extra.Name).Suggested); Assert.True(new ShopProjectFiles(Project, "target", extraFiles, null, null).NeedsReview);
        Assert.All(Select([up, a, b], (a, Pp()), (b, Pp())), f => Assert.False(f.Suggested));
        b = b with { Sha256 = a.Sha256 };
        Assert.All(Select([up, a, b], (a, Pp()), (b, Pp())), f => Assert.True(f.Suggested));
    }

    [Fact]
    public void DifferentProjectIsExcludedButMixedOrIncompleteHeadersAreNotGuessed()
    {
        var up = Up(); var map = Map();
        Assert.All(Select([up, map], (map, Pp(drawing: "other.dig"))), f => Assert.True(f.Ignored));
        var mixed = Pp(extra: [Page(("Files", ""), ("CNC file", up.Name), ("Drawing", "other.dig"), ("Sheet size", "1 x 2"), ("Tool setup", ""))]);
        Assert.All(Select([up, map], (map, mixed)), f => Assert.False(f.Suggested || f.Ignored));
        var missing = Pp(program: ""); Assert.All(Select([up, map], (map, missing)), f => Assert.False(f.Suggested));
    }

    [Fact]
    public void OnePdfCanCoverMultipleProgramsAndUnknownScannedPdfStillNeedsReview()
    {
        var a = Up("a.NC"); var b = Up("b.NC"); var map = Map();
        var document = Pp("a.NC", extra: [Page(("Files", ""), ("CNC file", "b.NC"), ("Drawing", "job.dig"), ("Sheet size", "1 x 2"), ("Tool setup", ""))]);
        Assert.All(Select([a, b, map], (map, document)), f => Assert.True(f.Suggested));
        var scan = Map("scan.pdf"); var files = Select([a, map, scan], (map, Pp("a.NC")), (scan, new(DocumentKind.Pdf, null, [], [Page()])));
        Assert.True(new ShopProjectFiles(Project, "target", files, null, null).NeedsReview);
    }

    [Fact]
    public void TechnicalDrawingIsNotAnAwjcMapAndFieldLabelsInsideFileNamesAreNotReadAgain()
    {
        var document = new SourceDocument(DocumentKind.Pdf, null, [], [Page(("Масштаб", "1:1"), ("Разраб.", "name"), ("Пров.", "name"), ("Лист", "1"))]);
        Assert.Equal(WorkflowWaterjetShopMaps.Kind.Unrelated, WorkflowWaterjetShopMaps.Analyze(Map(), document, Token).Type);
        var evidence = WorkflowWaterjetShopMaps.Analyze(Map(), Pp(drawing: "New Drawing file job.dig"), Token);
        Assert.Equal(["New Drawing file job.dig"], evidence.Drawings);
    }

    [AvaloniaFact]
    public async Task PublicDiscoveryUsesRealPdfTextAndExistingOneClickCopyWithoutChangingSource()
    {
        using var f = new ShopFixture("ГАР", true); File.Delete(f.Project.NativePath!); File.Delete(Path.Combine(f.Source, "Лист.nc")); File.Delete(Path.Combine(f.Source, "Карта.pdf"));
        var native = Path.Combine(f.Source, "job.dig"); await File.WriteAllTextAsync(native, "synthetic working project", Token);
        var project = f.Project with { NativePath = native }; var order = f.Order with { Projects = [project] }; var book = f.Book with { Orders = [order] };
        await File.WriteAllTextAsync(Path.Combine(f.Source, "renamed-sheet.NC"), "mock comments only", Token);
        var pdf = Path.Combine(f.Source, "renamed-sheet_pp.pdf");
        using (var writer = new PdfDocumentBuilder())
        {
            var font = writer.AddStandard14Font(Standard14Font.Helvetica); var page = writer.AddPage(612, 792);
            var y = 760;
            foreach (var (label, value) in new[] { ("Files", ""), ("CNC file", "stale.NC"), ("Drawing", "job.dig"), ("Sheet size", "1000 x 2000"), ("Tool setup", "") })
            { page.AddText(label, 10, new PdfPoint(25, y), font); if (value.Length > 0) page.AddText(value, 10, new PdfPoint(125, y), font); y -= 20; }
            await File.WriteAllBytesAsync(pdf, writer.Build(), Token);
        }
        var before = await ShopFileSystem.TreeAsync(f.Source, Token); string? copied = null;
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "test-db", order, book, _ => Task.CompletedTask, text => { copied = text; return Task.CompletedTask; });
        await model.RefreshAsync(); Assert.Null(model.Error); Assert.True(model.CanStartAutomatically);
        await model.CopyAsync(); Assert.Null(model.Error); Assert.True(model.AllPrepared); Assert.True(model.TextCopied); Assert.Contains("ГАР", copied);
        Assert.True(File.Exists(Path.Combine(model.Projects[0].Destination, "renamed-sheet.NC")));
        Assert.True(ShopFileSystem.SameTree(before, await ShopFileSystem.TreeAsync(f.Source, Token)));
    }
}
