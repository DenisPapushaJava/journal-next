using Jornal.Parsing.Documents;
using Jornal.Parsing.Specifications;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using System.Security.Cryptography;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowReportParserTests
{
    [Fact]
    public async Task HansPreparationNeedsOnlyMachineAndThicknessAndReportSelectsItsOwnTariffCategory()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var machine = book.Machines.Single(m => m.Name == "HansLaser");
        var steel = new CuttingRate(Guid.NewGuid(), machine.Name, "Нержавеющая сталь", 6, 320, 1) { MachineId = machine.Id };
        var metal = new CuttingRate(Guid.NewGuid(), machine.Name, "Цветные металлы", 8, 450, 1) { MachineId = machine.Id };
        await f.Store.WriteAsync(book.Version, w => w with
        { Rates = [.. w.Rates.Where(r => r.MachineId != machine.Id), steel, metal] }, TestContext.Current.CancellationToken);
        await f.Model.ReloadCommand.ExecuteAsync(null);
        f.Model.AddProjectCommand.Execute(null); f.Model.PlanMachine = f.Model.ProjectMachines.Single(m => m.Id == machine.Id);
        Assert.Equal(new[] { "6", "8" }, f.Model.PlanThicknessSuggestions);
        f.Model.PlanThickness = "6"; await f.Model.CreateProjectCommand.ExecuteAsync(null);
        Assert.Null(f.Model.Error); Assert.Empty(f.Model.Drafts); Assert.Equal("—", f.Model.SelectedProject!.Material);
        var project = f.Model.SelectedProject.Project; Assert.EndsWith(".Dsp", project.NativePath);
        Assert.Equal(0, new FileInfo(project.NativePath!).Length);
        book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var parsed = Report("hans-pdf/v1", "1");
        parsed = parsed with { Header = Value(new SpecificationHeader(DateOnly.FromDateTime(project.CreatedAt.DateTime),
            project.CreatedAt.ToString("yyyy-MM-dd-HHmmss"), project.CreatedAt.ToString("MMdd-HHmmss"), Path.GetFileName(project.NativePath)!)) };
        var bytes = new byte[] { 1, 2, 3 };
        var snapshot = new ReportFileSnapshot(project.NativePath + ".pdf", Convert.ToHexString(SHA256.HashData(bytes)), bytes, parsed);
        Assert.True(WorkflowReportFiles.Matches(snapshot, project, null));
        var candidate = new WorkflowReportCandidate(book, project, 1, [snapshot], "", () => { });
        Assert.Empty(candidate.Conflicts); Assert.True(candidate.Ready);
        Assert.Equal("Сплав", candidate.Draft!.Material); Assert.Equal("Нержавеющая сталь", candidate.Draft.MaterialGroup);
        Assert.Equal("320", candidate.Draft.PricePerMeter); Assert.Equal(steel.Id, candidate.Draft.Rate().Id);
    }

    private static SpecificationValue<T> Value<T>(T value) => new(value, []);

    [Theory]
    [InlineData("60 mm", "105223--HANSLASER.Dsp", 60, true)]
    [InlineData("60 mm", "105223--HANSLASER.Ds", 60, true)]
    [InlineData("60 mm", "105223--HANSLASER.", 60, true)]
    [InlineData("60 mm", "105223--HANS", 60, true)]
    [InlineData("60 mm", "105223", 60, true)]
    [InlineData("", "105223--HANS", 60, true)]
    [InlineData("60 mm", "10522", 60, false)]
    [InlineData("60 mm", "105224--HANSLASER.Ds", 60, false)]
    [InlineData("60 mm", "105223--HANSLASER.Dsx", 60, false)]
    [InlineData("60 mm", "105223--HANSLASER.Ds", 55, false)]
    [InlineData("55 mm", "105223--HANSLASER.Ds", 60, false)]
    public void HansReportMatchesWrappedAndClippedNameOnlyWithCompleteProjectIdentity(string prefix, string timeAndTail,
        int sheetThickness, bool expected)
    {
        var words = new List<PdfWord>();
        void Text(string text, double x, double y)
        {
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var width = word.Length * 4.0;
                words.Add(new(word, x, y, x + width, y + 8));
                x += width + 4;
            }
        }

        Text("Заказ", 30, 750); Text(prefix, 150, 750);
        Text("2026-11-11-" + timeAndTail, 150, 736);
        Text("Длина реза:(мм)", 30, 708); Text("5056.7", 150, 708);
        Text("Пробивки:", 30, 682); Text("20", 150, 682);
        Text("Листы", 30, 625);
        Text("№", 30, 600); Text("Размер X", 80, 600); Text("Размер Y", 160, 600);
        Text("Материал", 250, 600); Text("Gas", 340, 600); Text("Толщ. (мм)", 400, 600); Text("К-во", 500, 600);
        Text("1", 30, 575); Text("130", 90, 575); Text("1500", 170, 575);
        Text("St-45_0", 260, 575); Text("O2", 340, 575);
        Text(sheetThickness.ToString(System.Globalization.CultureInfo.InvariantCulture), 410, 575); Text("1", 500, 575);
        Text("Детали", 30, 550);

        var report = WorkflowReportFiles.Parse(new(DocumentKind.Pdf, null, [], [new(1, 595, 841, words)]), TestContext.Current.CancellationToken);
        const string name = "60 mm 2026-11-11-105223--HANSLASER";
        var native = Path.Combine(Path.GetTempPath(), name + ".Dsp");
        var project = new CuttingProject(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), 60,
            name, native, HansPdfSpecificationParser.Profile, DateTimeOffset.Now);
        var snapshot = new ReportFileSnapshot(Path.ChangeExtension(native, ".pdf"), "", [], report);

        Assert.Equal(HansPdfSpecificationParser.Profile, report.Profile);
        Assert.Equal(expected, WorkflowReportFiles.Matches(snapshot, project, null));
        if (expected) Assert.Equal("1111-105223", report.Header!.Value.JournalNumber);
    }

    private static SpecificationParseResult Report(string profile, string? materialId) =>
        new(profile, Value(new SpecificationHeader(new(2026, 10, 24), "2026-10-24-123456", "1024-123456", "project")),
            Value(profile == "hans-pdf/v1" ? "HansLaser" : "Лазер"), Value("Сплав"), Value(new SheetDimensions(6, 3000, 1500)),
            Value(1), Value(10000m), Value(20m), Value(2), Value(5), null, [])
        { IsRecognized = true, MaterialId = materialId is null ? null : Value(materialId) };

    [Theory]
    [InlineData("-1", "Базовая")] [InlineData("0", "Базовая")]
    [InlineData("1", "Нержавеющая сталь")] [InlineData("2", "Цветные металлы")]
    [InlineData(null, "")]
    public void HansTariffCategoryUsesReportedIdAndMissingIdRequiresExplicitChoice(string? id, string expected)
    {
        var draft = WorkflowViewModel.FromReport(Report("hans-pdf/v1", id), Path.Combine(Path.GetTempPath(), "hans.pdf"), WorkflowMachine.Defaults());
        var machine = WorkflowMachine.Defaults().Single(m => m.Name == "HansLaser"); draft.UseMachine(machine);
        Assert.True(draft.UsesMaterialCategories); Assert.Equal(expected, draft.MaterialGroup);
        var rate = new CuttingRate(Guid.NewGuid(), machine.Name, id is null ? "Базовая" : expected, 6, 100, 1) { MachineId = machine.Id };
        draft.ApplyRate(rate);
        if (id is null)
        {
            Assert.False(draft.CanSave); draft.MaterialGroup = "Базовая"; draft.ApplyRate(rate);
        }
        Assert.True(draft.CanSave);
    }

    [Theory]
    [InlineData("block-html/v1")] [InlineData("waterjet-awjc/v1")]
    public void NonHansMvpProfilesUseBaseCategoryRegardlessOfMaterialGradeOrUnrelatedId(string profile)
    {
        var draft = WorkflowViewModel.FromReport(Report(profile, "2"), Path.Combine(Path.GetTempPath(), "report.html"), WorkflowMachine.Defaults());
        Assert.False(draft.UsesMaterialCategories); Assert.Equal("Базовая", draft.MaterialGroup);
    }

    [Theory]
    [InlineData("170027", "1008-170027")]
    [InlineData("1700", "1008-1700")]
    public void AwjcSecondsRemainCompleteAndMaterialAndMissingPiercesRequireReview(string time, string number)
    {
        var words = new List<PdfWord>();
        void Text(string text, double x, double y)
        {
            foreach (var word in text.Split(' ')) { var width = word.Length * 4.0; words.Add(new(word, x, y, x + width, y + 8)); x += width + 4; }
        }
        Text("Дата", 50, 720); Text("2030-12-31 15:00", 160, 720);
        Text("Имя файла", 50, 690); Text($"5 мм 2026-10-08-{time}--AWJC.dig", 160, 690);
        Text("Материал", 50, 672); Text("Mild Steel/Standard 5 mm Сталь 20/09Г2С", 160, 672);
        Text("Резка", 50, 400); Text("Материал", 330, 400);
        Text("Время резки", 50, 370); Text("10:40:57 (10,683)", 180, 370); Text("Общий раскрой", 330, 370); Text("1500x1300 mm", 460, 370);
        Text("Длинна реза", 50, 350); Text("161253 mm", 180, 350); Text("Толщина", 330, 350); Text("5,0", 460, 350);
        Text("Раскрой:1500х1300mm", 50, 330); Text("Количество", 190, 330); Text("листов: 1", 190, 318);
        Text("Общий вес", 330, 330); Text("75", 460, 330);
        var doc = new SourceDocument(DocumentKind.Pdf, null, [], [new(1, 600, 800, words),
            new(2, 600, 800, [new("5 мм 2026-10-08-170099--AWJC.dig", 50, 690, 500, 698)])]);
        var report = WorkflowReportFiles.Parse(doc, TestContext.Current.CancellationToken);
        Assert.Equal(number, report.Header!.Value.JournalNumber);
        Assert.Contains("2026-10-08-" + time + "--AWJC.dig", report.Header.Value.OrderName);
        var draft = WorkflowViewModel.FromReport(report, Path.Combine(Path.GetTempPath(), "report.pdf"), WorkflowMachine.Defaults());
        var machine = WorkflowMachine.Defaults().Single(m => m.Name == "ГАР"); draft.UseMachine(machine);
        draft.ApplyRate(new(Guid.NewGuid(), machine.Name, "Базовая", 5, 100, 1) { MachineId = machine.Id });
        Assert.True(draft.MaterialNeedsReview); Assert.False(draft.CanSave); Assert.Empty(draft.Pierces);
        draft.MaterialConfirmed = true; Assert.False(draft.CanSave);
        draft.Pierces = "5"; Assert.True(draft.CanSave);
        Assert.Equal(number, draft.Number); Assert.Equal("08.10.2026", draft.Date);
        Assert.Contains("2026-10-08-" + time + "--AWJC.dig", string.Join(" ", doc.Pages[0].Words.Select(w => w.Text)));
    }

    [Theory]
    [InlineData(false, false, 10)]
    [InlineData(true, false, 31)]
    [InlineData(true, true, null)]
    public void AwjcReadsEnglishSummaryAndUsesOnlyCompleteDetailTables(bool continuation, bool brokenContinuation, int? expected)
    {
        var words = new List<PdfWord>();
        void Text(string text, double x, double y)
        {
            foreach (var word in text.Split(' '))
            {
                var width = word.Length * 4.0;
                words.Add(new(word, x, y, x + width, y + 8));
                x += width + 4;
            }
        }

        Text("Drawing file", 30, 720); Text("6 mm", 150, 720);
        Text("2026-11-12-131202--ГАР.di", 150, 707);
        Text("Material", 30, 686); Text("Mild Steel/Standard 6 mm", 150, 686);
        Text("Cutting Material", 30, 405);
        Text("Total time", 30, 375); Text("00:10:00", 150, 375); Text("Enclosing", 345, 375); Text("1500x1300 mm", 455, 375);
        Text("Cutting length", 30, 350); Text("2963 mm", 150, 350); Text("Thickness", 345, 350); Text("6,0 mm", 455, 350);
        Text("Layout:1500x1300mm", 30, 325); Text("Sheets: 4", 190, 325);
        Text("Weight", 345, 325); Text("17,1 kg", 455, 325);
        void Details(int number, int quantity, string? punches)
        {
            Text("No.", 45, 280); Text("Name", 170, 280); Text("Quantity", 290, 280);
            Text("Pierces", 370, 280); Text("Weight", 455, 280); Text("Quality", 520, 280);
            Text(FormattableString.Invariant($"{number}"), 49, 250); Text("Part", 170, 250);
            Text(FormattableString.Invariant($"{quantity}"), 305, 250);
            if (punches is not null) Text(punches, 380, 250);
            Text("1", 465, 250); Text("Medium", 520, 250);
        }
        var pages = new List<PdfPage>();
        if (continuation)
        {
            pages.Add(new(1, 595, 841, words));
            words = [];
            Details(0, 2, "5"); pages.Add(new(2, 595, 841, words));
            words = [];
            Details(1, 3, brokenContinuation ? null : "7"); pages.Add(new(3, 595, 841, words));
        }
        else
        {
            Details(0, 2, "5"); pages.Add(new(1, 595, 841, words));
        }

        var report = WorkflowReportFiles.Parse(new(DocumentKind.Pdf, null, [], pages), TestContext.Current.CancellationToken);
        Assert.True(report.IsComplete, string.Join("\n", report.Issues.Select(issue => issue.Message)));
        Assert.Equal("1112-131202", report.Header!.Value.JournalNumber);
        Assert.Equal("Mild Steel/Standard", report.Material!.Value);
        Assert.DoesNotContain(report.Issues, issue => issue.Code == "MaterialNeedsReview");
        Assert.Equal(4, report.SheetCount!.Value);
        Assert.Equal(17.1m, report.WeightKilograms!.Value);
        Assert.Equal(expected, report.PunchCount?.Value);
        const string nativeName = "6 мм 2026-11-12-131202--AWJC.dig";
        var native = Path.Combine(Path.GetTempPath(), nativeName);
        var project = new CuttingProject(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), 6,
            Path.GetFileNameWithoutExtension(native), native, WaterjetPdfSpecificationParser.Profile, DateTimeOffset.Now);
        var snapshot = new ReportFileSnapshot(Path.ChangeExtension(native, ".pdf"), "", [], report);
        Assert.True(WorkflowReportFiles.Matches(snapshot, project, null));
        Assert.False(WorkflowReportFiles.Matches(snapshot, project with { ThicknessMm = 5 }, null));
        Assert.False(WorkflowReportFiles.Matches(snapshot, project with { NativePath = native.Replace("131202", "131203", StringComparison.Ordinal) }, null));
    }
}
