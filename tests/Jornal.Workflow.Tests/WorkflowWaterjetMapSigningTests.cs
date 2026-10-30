using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using Pig = UglyToad.PdfPig.PdfDocument;
using Xunit;

namespace Jornal.Workflow.Tests;

internal static class WaterjetMapFixture
{
    internal static string Create(MapSigningFixture f, string name = "Карта_pp.pdf", string? native = null,
        string caption = "", bool annotation = false, bool continuation = false, bool form = true, int maps = 1)
    {
        var path = Path.Combine(f.Root, name); using var pdf = new PdfDocument();
        for (var n = 0; n < maps; n++)
        {
            var page = pdf.AddPage(); page.Width = XUnit.FromPoint(612); page.Height = XUnit.FromPoint(792);
            using var g = XGraphics.FromPdfPage(page); var font = WorkflowPdfFont.Create(9);
            void Text(string text, double x, double y) => g.DrawString(text, font, XBrushes.Black, new XPoint(x, y));
            if (form)
            {
                using var drawing = new XForm(pdf, XUnit.FromPoint(200), XUnit.FromPoint(100));
                using (var image = XGraphics.FromForm(drawing)) { image.DrawRectangle(XPens.Blue, 1, 1, 190, 95); image.DrawEllipse(XPens.Black, 20, 20, 60, 60); }
                g.DrawImage(drawing, 150, 130);
            }
            Text("Files", 72, 300); Text("CNC file", 74, 320); Text("Лист1.NC", 153, 320);
            Text("Drawing", 74, 338); Text(native ?? Path.GetFileName(f.Project.NativePath)!, 153, 338);
            for (var row = 0; row <= 4; row++) g.DrawRectangle(XBrushes.Black, 72, 360 + row * 12, 455, .4);
            foreach (var x in new[] { 72d, 150d, 527d }) g.DrawRectangle(XBrushes.Black, x, 360, .4, 48);
            Text("Material", 74, 370); Text("Mild Steel/Standard 4 mm", 153, 370);
            Text("НОМЕР ЗАКАЗА", 74, 382); Text("Sheet size", 74, 394); Text("1000 x 2000 mm", 153, 394);
            Text("Cut area total", 74, 406); Text("900 x 1800", 153, 406); Text("Tool setup", 72, 450);
            Text("Quantity 4 Quality Medium", 72, 480); Text("Cutting length 10 Weight 12", 72, 500);
            Text("Single time 00:10 Total time 00:40", 72, 520);
            if (caption.Length > 0)
            {
                if (!annotation) Text(caption, 153, 382);
                else
                {
                    var old = new PdfTextAnnotation { Contents = caption, Rectangle = new(new XPoint(149, 400), new XPoint(557, 422)) };
                    old.Elements.SetName("/Subtype", "/FreeText"); page.Annotations.Add(old);
                }
            }
            page.Annotations.Add(new PdfTextAnnotation { Contents = "Снять заусенцы", Rectangle = new(new XPoint(320, 650), new XPoint(450, 680)) });
        }
        if (continuation)
        {
            var p = pdf.AddPage(); using var g = XGraphics.FromPdfPage(p);
            g.DrawString("Quantity 2 Quality Fine Cutting length 8 Weight 2 Single time 00:02 Total time 00:04",
                WorkflowPdfFont.Create(9), XBrushes.Black, new XPoint(50, 50));
        }
        pdf.Save(path); return path;
    }
}

public sealed class WorkflowWaterjetMapSigningTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SignsMapsWithFilledBordersKeepsFormsAndContinuationAndCorrectionDoesNotAccumulateText(bool withForm, bool legacyAnnotation)
    {
        using var f = new MapSigningFixture("ГАР");
        var path = WaterjetMapFixture.Create(f, caption: legacyAnnotation ? "(4 мм) 1028-1234, Иванов, (Пример), БЛЦБ-000001" : "", annotation: legacyAnnotation, continuation: true, form: withForm, maps: 2);
        var before = await File.ReadAllBytesAsync(path, Token);
        var first = Assert.Single(await f.Sign()); Assert.True(first.Status == MapSigningStatus.Signed, first.Message); Assert.Equal(2, first.Pages);
        Assert.Equal(before, await File.ReadAllBytesAsync(first.BackupPath!, Token));
        using var original = Pig.Open(before);
        using (var after = Pig.Open(path))
        {
            Assert.Equal(3, after.NumberOfPages); Assert.Equal(original.GetPage(3).Text, after.GetPage(3).Text);
            foreach (var page in after.GetPages().Take(2)) Assert.Contains("БЛЦБ-009337", page.Text);
        }
        var time = File.GetLastWriteTimeUtc(path); var signed = await File.ReadAllBytesAsync(path, Token);
        Assert.Equal(MapSigningStatus.Unchanged, Assert.Single(await f.Sign()).Status);
        Assert.Equal(time, File.GetLastWriteTimeUtc(path)); Assert.Equal(signed, await File.ReadAllBytesAsync(path, Token));
        f.Book = f.Book with { Orders = [f.Order with { Blcb = "009338" }] };
        Assert.Equal(MapSigningStatus.Signed, Assert.Single(await f.Sign()).Status);
        using (var after = Pig.Open(path))
        {
            Assert.DoesNotContain("009337", after.GetPage(1).Text); Assert.DoesNotContain("000001", after.GetPage(1).Text);
            Assert.Contains("009338", after.GetPage(1).Text);
        }
        using var sharp = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        Assert.All(sharp.Pages.Cast<PdfPage>().Take(2), p => Assert.Equal("Снять заусенцы", Assert.Single(p.Annotations.Cast<PdfAnnotation>()).Contents));
        Assert.Equal(MapSigningStatus.Unchanged, Assert.Single(await f.Sign()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingSourceCaptionNeverWritesOrCreatesBackups(bool annotation)
    {
        using var f = new MapSigningFixture("ГАР"); var path = WaterjetMapFixture.Create(f, caption: f.Signature, annotation: annotation);
        var bytes = await File.ReadAllBytesAsync(path, Token); var time = File.GetLastWriteTimeUtc(path);
        var result = Assert.Single(await f.Sign()); Assert.True(result.Status == MapSigningStatus.Unchanged, result.Message);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token)); Assert.Equal(time, File.GetLastWriteTimeUtc(path));
        Assert.False(Directory.Exists(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder)));
    }

    [Fact]
    public async Task ExistingPlainCaptionCanBeReplacedWhenSafelySeparable()
    {
        using var f = new MapSigningFixture("ГАР"); var path = WaterjetMapFixture.Create(f, caption: "Старая подпись", form: false);
        var result = Assert.Single(await f.Sign()); Assert.True(result.Status == MapSigningStatus.Signed, result.Message);
        using var pdf = Pig.Open(path); Assert.DoesNotContain("Старая подпись", pdf.GetPage(1).Text);
    }

    [Fact]
    public async Task EditedStreamWithDrawingKeepsTheWholeMapInsteadOfDeletingMergedContents()
    {
        using var f = new MapSigningFixture("ГАР"); var path = WaterjetMapFixture.Create(f);
        Assert.Equal(MapSigningStatus.Signed, Assert.Single(await f.Sign()).Status);
        using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var page = pdf.Pages[0];
            using (var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append)) g.DrawRectangle(XPens.Red, 80, 100, 200, 80);
            page.Contents.Elements.GetDictionary(page.Contents.Elements.Count - 1)!.Elements.SetString("/JournalNextMapSignature", WorkflowShopDiscovery.Key(Path.GetFileName(f.Project.NativePath!)));
            pdf.Save(path);
        }
        f.Book = f.Book with { Orders = [f.Order with { Blcb = "009338" }] };
        var bytes = await File.ReadAllBytesAsync(path, Token);
        var result = Assert.Single(await f.Sign()); Assert.Equal(MapSigningStatus.Failed, result.Status);
        Assert.Contains("объединена", result.Message); Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token));
    }

    [Fact]
    public async Task EmptyFormDictionaryWithAnnotationFontsDoesNotBlockOrdinarySigning()
    {
        using var f = new MapSigningFixture("ГАР"); var path = WaterjetMapFixture.Create(f);
        using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            var form = new PdfDictionary(pdf); form.Elements["/Fields"] = new PdfArray(pdf); form.Elements["/DR"] = new PdfDictionary(pdf);
            pdf.Internals.Catalog.Elements["/AcroForm"] = form; pdf.Save(path);
        }
        Assert.Equal(MapSigningStatus.Signed, Assert.Single(await f.Sign()).Status);
        Assert.Equal(MapSigningStatus.Unchanged, Assert.Single(await f.Sign()).Status);
    }

    [Fact]
    public async Task DifferentProjectIsIgnoredAndMixedDocumentIsNeverPartlySigned()
    {
        using var f = new MapSigningFixture("ГАР"); var foreign = WaterjetMapFixture.Create(f, "другой.pdf", native: "чужой.dig");
        var bytes = await File.ReadAllBytesAsync(foreign, Token);
        Assert.Equal(MapSigningStatus.Skipped, Assert.Single(await f.Sign()).Status); Assert.Equal(bytes, await File.ReadAllBytesAsync(foreign, Token));
        var current = WaterjetMapFixture.Create(f);
        using (var original = PdfReader.Open(foreign, PdfDocumentOpenMode.Import))
        using (var map = PdfReader.Open(current, PdfDocumentOpenMode.Modify)) { map.AddPage(original.Pages[0]); map.Save(current); }
        bytes = await File.ReadAllBytesAsync(current, Token);
        Assert.Equal(MapSigningStatus.Failed, Assert.Single(await f.Sign()).Status); Assert.Equal(bytes, await File.ReadAllBytesAsync(current, Token));
    }

    [Theory]
    [InlineData("annotation")]
    [InlineData("unknown-page")]
    [InlineData("fields")]
    [InlineData("rotate")]
    [InlineData("long")]
    public async Task AmbiguousUnsupportedOrNonFittingMapIsUnchanged(string scenario)
    {
        using var f = new MapSigningFixture("ГАР"); var path = WaterjetMapFixture.Create(f);
        using (var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            if (scenario == "annotation") pdf.Pages[0].Annotations.Add(new PdfTextAnnotation { Contents = "Проверить вручную", Rectangle = new(new XPoint(180, 410), new XPoint(400, 420)) });
            if (scenario == "unknown-page") pdf.AddPage();
            if (scenario == "rotate") pdf.Pages[0].Rotate = 90;
            if (scenario == "fields")
            {
                var form = new PdfDictionary(pdf); var fields = new PdfArray(pdf); fields.Elements.Add(new PdfDictionary(pdf));
                form.Elements["/Fields"] = fields; pdf.Internals.Catalog.Elements["/AcroForm"] = form;
            }
            pdf.Save(path);
        }
        if (scenario == "long") f.Book = f.Book with { Customers = [f.Book.Customers[0] with { Name = new('Я', 350) }] };
        var bytes = await File.ReadAllBytesAsync(path, Token); Assert.Equal(MapSigningStatus.Failed, Assert.Single(await f.Sign()).Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token)); Assert.False(Directory.Exists(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder)));
    }

    [Fact]
    public async Task NoBlcbAndSourceConflictNeverWriteAndSignedMapRemainsEligibleForShop()
    {
        using var f = new MapSigningFixture("ГАР"); var path = WaterjetMapFixture.Create(f); var original = await File.ReadAllBytesAsync(path, Token); var book = f.Book;
        f.Book = f.Book with { Orders = [f.Order with { Blcb = null }] };
        Assert.Contains("БЛЦБ", Assert.Single(await f.Sign()).Message); Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
        f.Book = book;
        var rejected = Assert.Single(await f.Sign(validate: _ => { File.WriteAllText(path, "external"); return Task.CompletedTask; }));
        Assert.Equal(MapSigningStatus.Failed, rejected.Status); Assert.Equal("external", await File.ReadAllTextAsync(path, Token));
        await File.WriteAllBytesAsync(path, original, Token); Assert.Equal(MapSigningStatus.Signed, Assert.Single(await f.Sign()).Status);
        await File.WriteAllTextAsync(Path.Combine(f.Root, "Лист1.NC"), "program", Token);
        var root = Path.Combine(f.Root, "..", "awjc-shop-" + Guid.NewGuid().ToString("N"));
        var plan = Assert.Single(await WorkflowShopDiscovery.DiscoverAsync(root, f.Order, f.Book, Token));
        Assert.False(plan.NeedsReview); Assert.All(plan.Files, file => Assert.True(file.Suggested));
    }
}
