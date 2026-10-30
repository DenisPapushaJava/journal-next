using System.Security.Cryptography;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using PdfDocument = UglyToad.PdfPig.PdfDocument;
using Xunit;

namespace Jornal.Workflow.Tests;

internal sealed class MapSigningFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "workflow-map-signing-" + Guid.NewGuid().ToString("N"));
    internal WorkflowWorkspace Book { get; set; }
    internal WorkflowOrder Order => Book.Orders.Single();
    internal CuttingProject Project => Order.Projects[0];
    internal MapSigningFixture(string machineName = "Лазер")
    {
        Directory.CreateDirectory(Root);
        var machine = WorkflowMachine.Defaults().Single(m => m.LegacyName == machineName);
        var employee = new Employee(Guid.NewGuid(), "Юрченко"); var customer = new Customer(Guid.NewGuid(), "ЗПТО");
        var project = new CuttingProject(Guid.NewGuid(), Guid.NewGuid(), 1, machine.Id, 4m, "4 мм 2026-10-29-121314--" + WorkflowProjectNames.Tag(machine),
            Path.Combine(Root, "4 мм 2026-10-29-121314--" + WorkflowProjectNames.Tag(machine) + WorkflowProjectNames.Extension(machine)), machine.ImportProfile, DateTimeOffset.Now);
        File.WriteAllText(project.NativePath!, "project");
        var parameters = new CuttingParameters(machine.Name, "Ст3", "Базовая", 4m, "Лист", 1, 10, 2, 12, 60, 1, false, null) { MachineId = machine.Id };
        var state = new SpecificationState("1029-121314", new(2026, 10, 29), DateTimeOffset.Now, employee.Id, parameters,
            new(Guid.NewGuid(), 1, 1, 10, 2, 12), null, machine.ImportProfile);
        var order = new WorkflowOrder(Guid.NewGuid(), 1, customer.Id, employee.Id, employee.Id, "009337", 1, DateTimeOffset.Now, DateTimeOffset.Now,
            [new(project.Id, state)])
        { ReferenceNumber = "2026-10-29-121314", Projects = [project], Items = [new(project.ItemId, 1, project.Id)] };
        Book = WorkflowWorkspace.Empty with { Employees = [employee], Customers = [customer], Orders = [order] };
    }
    internal string Signature => WorkflowProjectComment.Format(Order, Project, Book.Employees, Book.Customers);
    internal Task<MapSigningResult[]> Sign(Guid? project = null, Func<CancellationToken, Task>? validate = null) =>
        WorkflowMapSigning.SignAsync(Order, Book, project, validate ?? (_ => Task.CompletedTask), TestContext.Current.CancellationToken);

    internal string Create(string name = "Карта.pdf", string comment = "", int maps = 1, bool continuation = false, bool note = false, string? native = null, string? station = null, string? cnc = null)
    {
        var path = Path.Combine(Root, name);
        using var pdf = new PdfSharp.Pdf.PdfDocument();
        for (var i = 0; i < maps; i++)
        {
            var page = pdf.AddPage(); page.Width = XUnit.FromPoint(595); page.Height = XUnit.FromPoint(842);
            using var g = XGraphics.FromPdfPage(page); var font = WorkflowPdfFont.Create(10);
            void Text(string value, double x, double y) => g.DrawString(value, font, XBrushes.Black, new XPoint(x, y));
            g.DrawRectangle(XPens.Black, 12, 15, 570, 60);
            g.DrawLine(XPens.Black, 12, 35, 582, 35); g.DrawLine(XPens.Black, 12, 55, 582, 55);
            g.DrawLine(XPens.Black, 90, 15, 90, 55); g.DrawLine(XPens.Black, 410, 15, 410, 55);
            g.DrawLine(XPens.Black, 190, 55, 190, 75);
            Text("Станок", 16, 29); Text(station ?? (Project.Name.EndsWith("ПЛАЗМА", StringComparison.Ordinal) ? "Кристалл Плазма" :
                Project.Name.EndsWith("ГАЗО-КИСЛОРОДНАЯ", StringComparison.Ordinal) ? "Кристалл 260 + ГАЗ" : "3-Лазер"), 96, 29);
            Text("Дата", 415, 29); Text("29.10.2026", 460, 29);
            Text("Проект", 16, 49); Text(native ?? Path.GetFileName(Project.NativePath)!, 96, 49);
            Text("CNC", 415, 49); Text(cnc ?? $"Лист{i + 1}" + (Project.Name.EndsWith("ЛАЗЕР", StringComparison.Ordinal) ? ".cnc" : ".nc"), 450, 49);
            Text("Комментарий", 16, 69); if (comment.Length > 0) Text(comment, 194, 69);
            g.DrawRectangle(XPens.Blue, 20, 100, 500, 300); g.DrawEllipse(XPens.Black, 40, 125, 85, 45);
            Text("Материал Ст3", 20, 440); Text("Название детали Кол-во Всего", 20, 470);
            Text("Деталь А 25 25", 20, 490);
            if (note)
            {
                var annotation = new PdfTextAnnotation { Title = "Технолог", Contents = "Снять заусенцы", Rectangle = new(new XPoint(400, 200), new XPoint(430, 230)) };
                page.Annotations.Add(annotation);
            }
        }
        if (continuation)
        {
            var page = pdf.AddPage();
            using var g = XGraphics.FromPdfPage(page);
            g.DrawString("Название детали Кол-во Всего", WorkflowPdfFont.Create(10), XBrushes.Black, new XPoint(20, 30));
            g.DrawString("Продолжение таблицы 26 26", WorkflowPdfFont.Create(10), XBrushes.Black, new XPoint(20, 50));
        }
        pdf.Save(path); return path;
    }

    public void Dispose() => Directory.Delete(Root, true);
}

public sealed class WorkflowMapSigningTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("Лазер")]
    [InlineData("Плазма")]
    [InlineData("Газо-кислородная")]
    public async Task SignsAllNestingPagesKeepsContinuationGeometryAndUserAnnotation(string machine)
    {
        using var f = new MapSigningFixture(machine); var path = f.Create(maps: 2, continuation: true, note: true);
        var before = await File.ReadAllBytesAsync(path, Token);
        var result = Assert.Single(await f.Sign()); Assert.Equal(MapSigningStatus.Signed, result.Status); Assert.Equal(2, result.Pages);
        Assert.Equal(before, await File.ReadAllBytesAsync(result.BackupPath!, Token));
        using var pdf = PdfDocument.Open(path); Assert.Equal(3, pdf.NumberOfPages);
        foreach (var page in pdf.GetPages().Take(2))
        { Assert.Contains("БЛЦБ-009337", page.Text); Assert.Contains("1029-121314", page.Text); Assert.Contains("Деталь А", page.Text); }
        Assert.DoesNotContain("БЛЦБ", pdf.GetPage(3).Text);
        using var a = PdfDocument.Open(before);
        Assert.Equal(a.GetPage(3).Text, pdf.GetPage(3).Text);
        Assert.Equal(a.GetPage(1).Paths.Count, pdf.GetPage(1).Paths.Count);
        using var sharp = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        Assert.Equal("Снять заусенцы", sharp.Pages[0].Annotations[0].Contents);
    }

    [Fact]
    public async Task PlasmaUsesItsDatabaseMachineAndMatchingProjectDespiteSharedCrystalGasLabel()
    {
        using var f = new MapSigningFixture("Плазма");
        var path = f.Create(maps: 4, continuation: true, note: true, station: "Кристалл 260 + ГАЗ");
        var original = await File.ReadAllBytesAsync(path, Token);
        var first = Assert.Single(await f.Sign());
        Assert.True(first.Status == MapSigningStatus.Signed, first.Message); Assert.Equal(4, first.Pages);
        Assert.Equal(original, await File.ReadAllBytesAsync(first.BackupPath!, Token));
        using (var pdf = PdfDocument.Open(path))
        {
            Assert.Equal(5, pdf.NumberOfPages);
            foreach (var page in pdf.GetPages().Take(4)) Assert.Contains("БЛЦБ-009337", page.Text);
            Assert.DoesNotContain("БЛЦБ", pdf.GetPage(5).Text);
        }
        var bytes = await File.ReadAllBytesAsync(path, Token); var time = File.GetLastWriteTimeUtc(path);
        var backups = Directory.GetFiles(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder), "*", SearchOption.AllDirectories).Length;
        Assert.Equal(MapSigningStatus.Unchanged, Assert.Single(await f.Sign()).Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token)); Assert.Equal(time, File.GetLastWriteTimeUtc(path));
        Assert.Equal(backups, Directory.GetFiles(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder), "*", SearchOption.AllDirectories).Length);
        f.Book = f.Book with { Orders = [f.Order with { Blcb = "009338", Version = f.Order.Version + 1 }] };
        var correction = Assert.Single(await f.Sign()); Assert.Equal(MapSigningStatus.Signed, correction.Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(correction.BackupPath!, Token));
        using (var pdf = PdfDocument.Open(path))
            foreach (var page in pdf.GetPages().Take(4))
            { Assert.Contains("БЛЦБ-009338", page.Text); Assert.DoesNotContain("БЛЦБ-009337", page.Text); }
        using var sharp = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        Assert.Equal("Снять заусенцы", sharp.Pages[0].Annotations[0].Contents);
    }

    [Theory]
    [InlineData("Лазер", "Лист.nc")]
    [InlineData("Газо-кислородная", "Лист.cnc")]
    [InlineData("Плазма", "Лист.cnc")]
    public async Task BlockMapStillRejectsTheWrongProgramExtension(string machine, string cnc)
    {
        using var f = new MapSigningFixture(machine); var path = f.Create(station: "Рабочий станок", cnc: cnc);
        var before = await File.ReadAllBytesAsync(path, Token);
        var result = Assert.Single(await f.Sign()); Assert.Equal(MapSigningStatus.Failed, result.Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, Token)); Assert.False(Directory.Exists(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder)));
    }

    [Theory]
    [InlineData("Лазер", "Fiber laser №2")]
    [InlineData("Газо-кислородная", "Кристалл 260 / кислород")]
    [InlineData("Плазма", "Hypertherm 130A")]
    public async Task BlockMapUsesItsMatchingProjectAndProgramInsteadOfVariableStationNames(string machine, string station)
    {
        using var f = new MapSigningFixture(machine); f.Create(station: station);
        var result = Assert.Single(await f.Sign()); Assert.True(result.Status == MapSigningStatus.Signed, result.Message);
    }

    [Fact]
    public async Task IdenticalSignatureIsANoOpAndDifferentOneIsActuallyRemoved()
    {
        using var f = new MapSigningFixture(); var path = f.Create(comment: "Старая подпись БЛЦБ-000001");
        Assert.Equal(MapSigningStatus.Signed, Assert.Single(await f.Sign()).Status);
        using (var pdf = PdfDocument.Open(path)) Assert.DoesNotContain("000001", pdf.GetPage(1).Text);
        var bytes = await File.ReadAllBytesAsync(path, Token); var date = File.GetLastWriteTimeUtc(path);
        Assert.Equal(MapSigningStatus.Unchanged, Assert.Single(await f.Sign()).Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token)); Assert.Equal(date, File.GetLastWriteTimeUtc(path));
        Assert.Single(Directory.GetFiles(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder), "*.pdf", SearchOption.AllDirectories));
        f.Book = f.Book with { Orders = [f.Order with { Blcb = "009338" }] };
        var changed = Assert.Single(await f.Sign()); Assert.Equal(MapSigningStatus.Signed, changed.Status);
        using var next = PdfDocument.Open(path); Assert.Contains("009338", next.GetPage(1).Text); Assert.DoesNotContain("009337", next.GetPage(1).Text);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder), "*.pdf", SearchOption.AllDirectories).Length);
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(900, false)]
    public async Task LongSignatureFitsWithoutTruncationOrLeavesOriginalUntouched(int length, bool fits)
    {
        using var f = new MapSigningFixture(); var path = f.Create(); var original = await File.ReadAllBytesAsync(path, Token);
        f.Book = f.Book with { Customers = [f.Book.Customers[0] with { Name = string.Join(" ", Enumerable.Repeat("Заказчик", length / 8)) }] };
        var result = Assert.Single(await f.Sign());
        Assert.True(result.Status == (fits ? MapSigningStatus.Signed : MapSigningStatus.Failed), result.Message);
        if (!fits) { Assert.Contains("слишком длинная", result.Message); Assert.Equal(original, await File.ReadAllBytesAsync(path, Token)); }
        else
        {
            Assert.Equal(MapSigningStatus.Unchanged, Assert.Single(await f.Sign()).Status);
            f.Book = f.Book with { Orders = [f.Order with { Blcb = "009338" }] };
            var correction = Assert.Single(await f.Sign()); Assert.True(correction.Status == MapSigningStatus.Signed, correction.Message);
        }
    }

    [Theory]
    [InlineData("Лазер")]
    [InlineData("Плазма")]
    public async Task ForeignPdfAndPreviousProjectAreNotChangedAndMixedProjectFileIsBlocked(string machine)
    {
        using var f = new MapSigningFixture(machine); var station = machine == "Плазма" ? "Кристалл 260 + ГАЗ" : null;
        var foreign = f.Create("чужая карта.pdf", native: "Другой.rcam", station: station);
        var original = await File.ReadAllBytesAsync(foreign, Token); var path = f.Create(station: station);
        Assert.Equal(MapSigningStatus.Signed, Assert.Single(await f.Sign()).Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(foreign, Token));
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        using (var another = PdfReader.Open(foreign, PdfDocumentOpenMode.Import))
        { doc.AddPage(another.Pages[0]); doc.Save(path); }
        var mixed = await File.ReadAllBytesAsync(path, Token);
        var result = Assert.Single(await f.Sign()); Assert.Equal(MapSigningStatus.Failed, result.Status);
        Assert.Contains("Страница 2", result.Message); Assert.Equal(mixed, await File.ReadAllBytesAsync(path, Token));
    }

    [Fact]
    public async Task OneLockedOrCorruptPdfDoesNotPreventSigningOtherMaps()
    {
        using var f = new MapSigningFixture(); var path = f.Create(); var locked = f.Create("занятая.pdf");
        await File.WriteAllTextAsync(Path.Combine(f.Root, "ошибка.pdf"), "broken", Token);
        using (File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var results = await f.Sign(); Assert.Contains(results, r => r.FilePath == path && r.Status == MapSigningStatus.Signed);
            Assert.Equal(2, results.Count(r => r.Status == MapSigningStatus.Failed));
        }
    }

    [Fact]
    public async Task SourceChangeOrFailedRevalidationDoesNotOverwriteCurrentFile()
    {
        using var f = new MapSigningFixture(); var path = f.Create(); var original = await File.ReadAllBytesAsync(path, Token);
        var result = Assert.Single(await f.Sign(validate: _ => throw new WorkflowValidationException("Заявка изменилась")));
        Assert.Equal(MapSigningStatus.Failed, result.Status); Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
        var replacement = f.Create("внешняя версия.pdf", comment: "Изменено вручную"); var external = await File.ReadAllBytesAsync(replacement, Token);
        File.Delete(replacement);
        result = Assert.Single(await f.Sign(validate: _ => { File.WriteAllBytes(path, external); return Task.CompletedTask; }));
        Assert.Equal(MapSigningStatus.Failed, result.Status); Assert.Contains("не перезаписан", result.Message);
        Assert.Equal(external, await File.ReadAllBytesAsync(path, Token)); Assert.Empty(Directory.GetFiles(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder), "*.tmp"));
    }

    [Fact]
    public async Task UnsupportedMachineMissingSpecificationAndEmptyBlcbNeverAlterMaps()
    {
        using var f = new MapSigningFixture("Труборез"); var path = f.Create(); var original = await File.ReadAllBytesAsync(path, Token);
        Assert.Equal(MapSigningStatus.Skipped, Assert.Single(await f.Sign()).Status); Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
        using var supported = new MapSigningFixture(); path = supported.Create(); original = await File.ReadAllBytesAsync(path, Token);
        var book = supported.Book;
        supported.Book = book with { Orders = [supported.Order with { Specifications = [] }] };
        Assert.Contains("спецификацию", Assert.Single(await supported.Sign()).Message);
        supported.Book = book with { Orders = [book.Orders[0] with { Blcb = "" }] };
        Assert.Contains("БЛЦБ", Assert.Single(await supported.Sign()).Message); Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
    }

    [Fact]
    public async Task AllActionSkipsExcludedProjectButExplicitProjectActionCanSignIt()
    {
        using var f = new MapSigningFixture(); var path = f.Create(); var original = await File.ReadAllBytesAsync(path, Token);
        f.Book = f.Book with { Orders = [f.Order with { Items = [f.Order.Items[0] with { IsIncluded = false }] }] };
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Sign()); Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
        Assert.Equal(MapSigningStatus.Signed, Assert.Single(await f.Sign(f.Project.Id)).Status);
    }

    [Fact]
    public async Task SigningPreservesShopDiscoveryAndCopiedFileIsTheSignedVersion()
    {
        using var f = new MapSigningFixture(); var map = f.Create(); await File.WriteAllTextAsync(Path.Combine(f.Root, "Лист1.cnc"), "UP", Token);
        Assert.Equal(MapSigningStatus.Signed, Assert.Single(await f.Sign()).Status);
        var shop = Path.Combine(f.Root, "..", "shop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var plan = Assert.Single(await WorkflowShopDiscovery.DiscoverAsync(shop, f.Order, f.Book, Token));
            Assert.False(plan.NeedsReview); Assert.All(plan.Files, file => Assert.True(file.Suggested));
            var result = Assert.Single(await WorkflowShopTransfer.CopyAsync(shop, "test-db", f.Order.Id,
                [new(plan, plan.Files, false, null)], false, _ => Task.CompletedTask, token: Token));
            Assert.True(result.Success); Assert.Equal(await File.ReadAllBytesAsync(map, Token), await File.ReadAllBytesAsync(Path.Combine(plan.Destination, Path.GetFileName(map)), Token));
            Assert.False(Directory.Exists(Path.Combine(plan.Destination, WorkflowMapSigning.BackupFolder)));
        }
        finally { if (Directory.Exists(shop)) Directory.Delete(shop, true); }
    }

    [Fact]
    public async Task AlreadyCorrectSourceDoesNotCreateBackupOrChangeFileTimestamp()
    {
        using var f = new MapSigningFixture(); var path = f.Create(comment: f.Signature);
        var original = await File.ReadAllBytesAsync(path, Token); var modified = File.GetLastWriteTimeUtc(path);
        Assert.Equal(MapSigningStatus.Unchanged, Assert.Single(await f.Sign()).Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Token)); Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
        Assert.False(Directory.Exists(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder)));
    }

    [Theory]
    [InlineData("rotate")]
    [InlineData("annotation")]
    [InlineData("unknown-page")]
    [InlineData("crop")]
    public async Task UnsupportedPageOrAnnotationOverCommentLeavesWholePdfUntouched(string scenario)
    {
        using var f = new MapSigningFixture(); var path = f.Create();
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            if (scenario == "rotate") doc.Pages[0].Rotate = 90;
            if (scenario == "crop") doc.Pages[0].CropBox = new(new XPoint(10, 10), new XPoint(585, 832));
            if (scenario == "annotation") doc.Pages[0].Annotations.Add(new PdfTextAnnotation
            { Contents = "Ручная пометка поверх подписи", Rectangle = new(new XPoint(200, 770), new XPoint(400, 783)) });
            if (scenario == "unknown-page") doc.AddPage();
            doc.Save(path);
        }
        var bytes = await File.ReadAllBytesAsync(path, Token);
        Assert.Equal(MapSigningStatus.Failed, Assert.Single(await f.Sign()).Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token)); Assert.False(Directory.Exists(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder)));
    }

    [Fact]
    public void FamiliarCommentKeepsNumberSecondsLeadingZerosAndSingleLineRequisites()
    {
        using var f = new MapSigningFixture();
        f.Book = f.Book with
        {
            Customers = [f.Book.Customers[0] with { Name = "ЗПТО\r\n  СПб" }],
            Orders = [f.Order with { Blcb = "БЛЦБ-000123" }]
        };
        Assert.Equal("(4 мм)  1029-121314,  Юрченко,  (ЗПТО СПб),  БЛЦБ-000123", f.Signature);
    }
}
