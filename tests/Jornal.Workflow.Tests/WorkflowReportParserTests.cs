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
}
