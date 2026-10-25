using System.Security.Cryptography;
using System.Text.Json;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

internal sealed class ProjectFixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "workflow-projects-" + Guid.NewGuid().ToString("N"));
    public LocalWorkflowWorkspaceStore Store { get; }
    public WorkflowViewModel Model { get; }
    public ProjectFixture(Func<LocalWorkflowWorkspaceStore, WorkflowViewModel>? modelFactory = null)
    {
        Directory.CreateDirectory(DirectoryPath);
        Store = new(Path.Combine(DirectoryPath, "workspace.json"));
        Model = modelFactory?.Invoke(Store) ?? new(Store, new AppUpdatesViewModel(new WorkflowAppUpdater()));
    }
    public async Task InitializeAsync()
    {
        var book = WorkflowTestData.CreateWorkspace();
        book = WorkflowProjects.SetRoot(book, Path.Combine(DirectoryPath, "projects"));
        foreach (var machine in book.Machines)
            book = WorkflowChanges.SetRate(book, new(Guid.NewGuid(), machine.Name, "Базовая", 6, 100, 1) { MachineId = machine.Id });
        await Store.WriteAsync(0, _ => book);
        new WorkflowPreferencesStore(Path.Combine(DirectoryPath, "preferences.json")).Save(new() { EmployeeId = book.Employees[1].Id });
        await Model.ReloadCommand.ExecuteAsync(null); Model.NewManager = "Иванов"; Model.NewCustomer = "ООО «Пример»";
        await Model.CreateOrderCommand.ExecuteAsync(null); Assert.Null(Model.Error);
    }
    public async Task<CuttingProject> AddAsync(string machine = "Лазер", bool redo = false)
    {
        if (redo) Model.RedoProjectCommand.Execute(null); else Model.AddProjectCommand.Execute(null);
        Model.PlanMachine = Model.ProjectMachines.Single(m => m.Name == machine);
        Model.PlanThickness = "6";
        await Model.CreateProjectCommand.ExecuteAsync(null); Assert.Null(Model.Error);
        return Model.SelectedProject!.Project;
    }
    public static string Html(CuttingProject project, int length = 10000, string tail = "") => $$"""
        <html><meta charset="utf-8"><body>
        <table><tr><td>Спецификация № {{Path.GetFileName(project.NativePath)}}</td></tr></table>
        <table><tr><td>Листов:</td><td>1</td><td>Габариты:</td><td>6x3000x1500 1шт</td></tr></table>
        <table><tr><td>Материал:</td><td>Ст3 6 mm</td><td>Одна деталь</td><td>Все детали</td></tr></table>
        <table><tr><td>Всего по заказу:</td><td>Общие кол-во деталей:</td><td>2</td></tr>
        <tr><td>Длина реза:(мм)</td><td>{{length}}</td></tr><tr><td>Пробивки:</td><td>5</td></tr>
        <tr><td>Вес:(кг)</td><td>48.2</td></tr><tr><td>Время:(ч:мм)</td><td>00:10</td></tr></table>
        {{tail}}</body></html>
        """;
    public static string ReportPath(CuttingProject project) => project.NativePath + ".html";
    public static Task WriteAsync(CuttingProject project, int length = 10000, string tail = "") =>
        File.WriteAllTextAsync(ReportPath(project), Html(project, length, tail));
    public async Task<WorkflowReportReviewViewModel> ReviewAsync()
    {
        var review = await Model.CreateReportReviewAsync(); Assert.Null(Model.Error);
        return Assert.IsType<WorkflowReportReviewViewModel>(review);
    }
    public async Task AcceptAsync()
    {
        var review = await ReviewAsync(); Assert.True(review.SaveCommand.CanExecute(null));
        await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error); Assert.True(review.WasSaved);
    }
    public void Dispose() { Model.Detach(); Directory.Delete(DirectoryPath, true); }
}

public sealed class ProjectWorkflowTests
{
    [Fact]
    public async Task PreparedProjectHasNoFakeCalculationAndPartialAcceptanceKeepsItsIdentity()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var a = await f.AddAsync(); var b = await f.AddAsync("Плазма");
        Assert.Empty(f.Model.Drafts); Assert.False(f.Model.HasPendingEdits);
        Assert.All(f.Model.Projects, row => Assert.Equal("—", row.Material));
        Assert.Contains("--ЛАЗЕР", a.Name); Assert.Matches(@"6 мм \d{4}-\d{2}-\d{2}-\d{6}--ЛАЗЕР", a.Name);
        Assert.Equal(0, new FileInfo(a.NativePath!).Length);
        Assert.NotEqual(a.Name, b.Name);
        await ProjectFixture.WriteAsync(a);
        var review = await f.ReviewAsync();
        Assert.Equal(2, review.Candidates.Count); Assert.True(review.Candidates.Single(c => c.Project.Id == a.Id).Ready);
        Assert.Equal("Отчётов пока нет", review.Candidates.Single(c => c.Project.Id == b.Id).Status);
        await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error);
        var order = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single();
        var spec = Assert.Single(order.Specifications); Assert.Equal(a.Id, spec.Id); Assert.Equal(1105, order.CuttingTotal);
        Assert.Equal(1, order.PendingProjectCount); Assert.Equal(2, order.Items.Length);
        var report = Assert.IsType<AcceptedReport>(spec.Current.Report);
        Assert.Equal(await File.ReadAllBytesAsync(ProjectFixture.ReportPath(a), TestContext.Current.CancellationToken), await File.ReadAllBytesAsync(report.ArchivePath, TestContext.Current.CancellationToken));
        f.Model.GenerateResultCommand.Execute(null); Assert.NotNull(f.Model.Error); Assert.Empty(f.Model.ResultText);
    }

    [Fact]
    public async Task ChangedBytesWithSameSizeAndMtimeUpdateAcceptedSnapshotEvenWhenTotalsAreEqual()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        await ProjectFixture.WriteAsync(p, tail: "<!-- aaa -->"); await f.AcceptAsync();
        var old = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Specifications[0].Current;
        var source = ProjectFixture.ReportPath(p); var mtime = File.GetLastWriteTimeUtc(source); var size = new FileInfo(source).Length;
        f.Model.GenerateResultCommand.Execute(null); f.Model.ResultText = "Ручной текст\n";
        await ProjectFixture.WriteAsync(p, tail: "<!-- bbb -->"); File.SetLastWriteTimeUtc(source, mtime);
        Assert.Equal(size, new FileInfo(source).Length);
        await f.AcceptAsync();
        var spec = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Specifications[0];
        Assert.Single((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Specifications); Assert.Equal(old.Calculation, spec.Current.Calculation);
        Assert.NotEqual(old.Report!.Sha256, spec.Current.Report!.Sha256);
        Assert.True(File.Exists(old.Report.ArchivePath));
        Assert.Equal("Ручной текст\n", f.Model.ResultText); Assert.True(f.Model.ResultIsStale);
        var unchanged = await f.ReviewAsync();
        Assert.Equal("Без изменений", Assert.Single(unchanged.Candidates).Status);
        Assert.False(unchanged.SaveCommand.CanExecute(null));
        Assert.Equal(2, Directory.GetDirectories(Path.GetDirectoryName(spec.Current.Report.ArchivePath)! + "/..").Length);
    }

    [Fact]
    public async Task ManualOverridesAndHistoricalRateSurviveRefreshAndOnlyDisputedFieldNeedsChoice()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        await ProjectFixture.WriteAsync(p); await f.AcceptAsync();
        var draft = f.Model.SelectedDraft!; draft.Length = "15"; draft.Coefficient = "1,2"; draft.Comment = "Ручной комментарий"; draft.HasBending = true;
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var catalog = f.Model.CreateCatalogSettings(); catalog.SelectedMachine = catalog.Machines.Single(m => m.Name == "Лазер");
        catalog.SelectedRate = catalog.Rates!.Single(); catalog.SelectedRate.PricePerMeter = "900";
        await catalog.SaveCommand.ExecuteAsync(null); Assert.Null(catalog.Error);
        // Source length did not change: keep 15, not the imported 11, without a conflict.
        await ProjectFixture.WriteAsync(p, tail: "<!-- new layout -->");
        var review = await f.ReviewAsync(); var candidate = Assert.Single(review.Candidates);
        Assert.Empty(candidate.Conflicts); Assert.Equal("15", candidate.Draft!.Length); Assert.Equal("100", candidate.Draft.PricePerMeter);
        Assert.Equal("1,2", candidate.Draft.Coefficient); Assert.Equal("Ручной комментарий", candidate.Draft.Comment); Assert.True(candidate.Draft.HasBending);
        await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error);
        // Both source and manual length now differ from the original parsed 11.
        await ProjectFixture.WriteAsync(p, 20000);
        review = await f.ReviewAsync(); candidate = Assert.Single(review.Candidates);
        var conflict = Assert.Single(candidate.Conflicts); Assert.Equal("Длина реза", conflict.Field); Assert.False(candidate.Ready);
        await review.RefreshCommand.ExecuteAsync(null); Assert.Null(review.Error);
        candidate = Assert.Single(review.Candidates); conflict = Assert.Single(candidate.Conflicts);
        Assert.False(candidate.Ready); Assert.Equal("1,2", candidate.Draft!.Coefficient);
        Assert.Equal("15", conflict.Manual); Assert.Equal("21", conflict.Report);
        conflict.Selection = 1; Assert.True(candidate.Ready); Assert.Equal("21", candidate.Draft!.Length);
        Assert.Equal(2526, candidate.Draft.Quote!.TotalCost);
        await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error);
        var saved = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Specifications[0].Current;
        Assert.Equal("21", saved.Report!.Parsed.Length); Assert.Equal(1.2m, saved.Parameters.ManualCoefficient);
        Assert.Equal(100, saved.Calculation.PricePerMeter);
    }

    [Fact]
    public async Task SourceChangedAfterPreviewRejectsWholeBatchAndKeepsReviewEdits()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var a = await f.AddAsync(); var b = await f.AddAsync("Плазма");
        await ProjectFixture.WriteAsync(a); await ProjectFixture.WriteAsync(b);
        var review = await f.ReviewAsync(); var candidate = review.Candidates[0]; candidate.Draft!.Coefficient = "1,5";
        await ProjectFixture.WriteAsync(b, 20000);
        await review.SaveCommand.ExecuteAsync(null);
        Assert.Contains("изменился после подбора", review.Error); Assert.False(review.WasSaved);
        Assert.Empty((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Specifications);
        Assert.Equal("1,5", candidate.Draft.Coefficient); Assert.True(review.SaveCommand.CanExecute(null));
        await review.RefreshCommand.ExecuteAsync(null); Assert.Null(review.Error);
        Assert.Equal("1,5", review.Candidates.Single(c => c.Project.Id == a.Id).Draft!.Coefficient);
        Assert.Equal("21", review.Candidates.Single(c => c.Project.Id == b.Id).Draft!.Length);
        await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error); Assert.True(review.WasSaved);
        Assert.Equal(2, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Specifications.Length);
    }

    [Fact]
    public async Task AmbiguousReportsNeedExplicitChoiceAndWrongHeadersCannotBeMatchedByOuterName()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        await ProjectFixture.WriteAsync(p);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(p.NativePath)!, "исправленный.htm"), ProjectFixture.Html(p, 20000), TestContext.Current.CancellationToken);
        var review = await f.ReviewAsync(); var candidate = Assert.Single(review.Candidates);
        Assert.Equal(2, candidate.Choices.Count); Assert.Null(candidate.SelectedFile); Assert.False(candidate.Ready);
        candidate.SelectedFile = candidate.Choices.Single(c => c.Name == "исправленный.htm");
        Assert.True(candidate.Ready); Assert.Equal("21", candidate.Draft!.Length);
        await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error);
        File.Delete(Path.Combine(Path.GetDirectoryName(p.NativePath)!, "исправленный.htm"));
        await File.WriteAllTextAsync(ProjectFixture.ReportPath(p), ProjectFixture.Html(p).Replace(p.Name, "6 мм 2000-01-01-010101--ЛАЗЕР"), TestContext.Current.CancellationToken);
        review = await f.ReviewAsync(); Assert.Empty(Assert.Single(review.Candidates).Choices);
        Assert.Single((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Specifications);
    }

    [Fact]
    public async Task MachineVariantIsSeparateAndOnlyExplicitlySelectedVariantContributesToTotal()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var a = await f.AddAsync();
        await ProjectFixture.WriteAsync(a); await f.AcceptAsync();
        var b = await f.AddAsync("Плазма", redo: true);
        Assert.Equal(a.ItemId, b.ItemId); Assert.Equal(2, b.Variant);
        await f.Model.SelectProjectForTotalCommand.ExecuteAsync(null); Assert.NotNull(f.Model.Error);
        await ProjectFixture.WriteAsync(b, 20000); await f.AcceptAsync();
        var order = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0];
        Assert.Equal(2, order.Specifications.Length); Assert.Single(order.Items); Assert.Equal(1105, order.CuttingTotal);
        f.Model.SelectedProject = f.Model.Projects.Single(row => row.Project.Id == b.Id);
        await f.Model.SelectProjectForTotalCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        order = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0]; Assert.Equal(2105, order.CuttingTotal);
        Assert.Equal(b.Id, order.Items[0].CurrentProjectId); Assert.True(File.Exists(a.NativePath));
        f.Model.SelectedProject = f.Model.Projects.Single(row => row.Project.Id == a.Id);
        await f.Model.SelectProjectForTotalCommand.ExecuteAsync(null); f.Model.GenerateResultCommand.Execute(null);
        Assert.Null(f.Model.Error); Assert.NotEmpty(f.Model.ResultText);
        Assert.Equal(1105, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].CuttingTotal);
    }

    [Fact]
    public async Task SameOrderConflictPreservesPreviewAndUnrelatedOrderChangeDoesNotBlockAcceptance()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p);
        var preview = await f.ReviewAsync();
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var o = book.Orders.Single();
        await f.Store.WriteAsync(book.Version, w => WorkflowChanges.CreateOrder(w, "Другой заказчик", w.Employees[0].Id,
            w.Employees[1].Id, w.Employees[1].Id, DateTimeOffset.UtcNow, out _), TestContext.Current.CancellationToken);
        await preview.SaveCommand.ExecuteAsync(null); Assert.Null(preview.Error);
        await ProjectFixture.WriteAsync(p, 20000); preview = await f.ReviewAsync();
        book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); o = book.Orders.Single(order => order.Id == o.Id);
        await f.Store.WriteAsync(book.Version, w => WorkflowChanges.UpdateOrder(w, o.Id, o.Version, o.ManagerId,
            o.TechnologistId, "123", o.TechnologistId, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        await preview.SaveCommand.ExecuteAsync(null); Assert.NotNull(preview.Error); Assert.False(preview.WasSaved);
        Assert.NotNull(preview.Selected!.Draft);
        Assert.Single((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single(order => order.Id == o.Id).Specifications);
    }

    [Fact]
    public async Task UnselectedReadyAlternativeCannotTurnAnUncalculatedCurrentVariantIntoZeroPrice()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); await f.AddAsync();
        var alternative = await f.AddAsync("Плазма", redo: true);
        await ProjectFixture.WriteAsync(alternative); await f.AcceptAsync();
        Assert.Equal("Резка не рассчитана", f.Model.TotalText);
        Assert.Equal("—", Assert.Single(f.Model.Orders).Total);
        var order = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0];
        Assert.Single(order.Specifications); Assert.Empty(order.ActiveSpecifications); Assert.Equal(1, order.PendingProjectCount);
    }

    [Fact]
    public async Task ConcurrentNameReservationsAreUniqueAcrossThicknessesAndMachinesWithoutOverwrites()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = book.Orders[0];
        var time = new DateTimeOffset(2026, 10, 24, 14, 35, 27, TimeSpan.FromHours(3));
        var tasks = Enumerable.Range(0, 12).Select(index => Task.Run(() => WorkflowProjectFiles.PrepareAsync(book.ProjectRoot!, order,
            "Пример / : * ?", book.Machines[index % 4], index + 1, null, time, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        var projects = await Task.WhenAll(tasks);
        Assert.Equal(12, projects.Select(p => p.CreatedAt).Distinct().Count());
        Assert.All(projects, p => Assert.Equal(0, new FileInfo(p.NativePath!).Length));
        Assert.Equal(12, Directory.GetFiles(Path.Combine(book.ProjectRoot!, ".workflow-names")).Length);
    }

    [Fact]
    public async Task SchemaTwoUpgradeMakesItemsWithoutFabricatingNativeFilesAndBacksUpOriginal()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        await ProjectFixture.WriteAsync(p); await f.AcceptAsync();
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var old = book.Orders[0].Specifications[0];
        var schema2 = book with { SchemaVersion = 2, Orders = book.Orders.Select(o => o with { Items = [], Projects = [] }).ToArray() };
        var path = Path.Combine(f.DirectoryPath, "old-workspace.json"); var json = JsonSerializer.Serialize(schema2); await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
        var migrated = await new LocalWorkflowWorkspaceStore(path).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(11, migrated.SchemaVersion); var project = Assert.Single(migrated.Orders[0].Projects);
        Assert.Equal(old.Id, project.Id); Assert.Null(project.NativePath);
        Assert.Equal(old.Current, migrated.Orders[0].Specifications[0].Current);
        Assert.Equal(book.Orders[0].CuttingTotal, migrated.Orders[0].CuttingTotal);
        var backup = Assert.Single(Directory.GetFiles(f.DirectoryPath, "old-workspace.json.*.bak"));
        Assert.Equal(json, await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ArchivedReportCorruptionCannotBeSilentlyOverwrittenOrSaved()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p);
        var review = await f.ReviewAsync(); var candidate = Assert.Single(review.Candidates); var report = candidate.Snapshot!;
        var hash = Convert.ToHexString(SHA256.HashData(report.Bytes));
        var folder = Path.Combine(Path.GetDirectoryName(report.SourcePath)!, ".workflow-reports", hash);
        Directory.CreateDirectory(folder); var archive = Path.Combine(folder, Path.GetFileName(report.SourcePath));
        await File.WriteAllTextAsync(archive, "Испорченная копия", TestContext.Current.CancellationToken);
        await review.SaveCommand.ExecuteAsync(null); Assert.Contains("Архив отчёта повреждён", review.Error);
        Assert.Equal("Испорченная копия", await File.ReadAllTextAsync(archive, TestContext.Current.CancellationToken));
        Assert.Empty((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Specifications);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    [Fact]
    public async Task FirstHtmlReportSuppliesMaterialAndBaseTariffWithoutPreparationConflicts()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var p = await f.AddAsync();
        await File.WriteAllTextAsync(ProjectFixture.ReportPath(p), ProjectFixture.Html(p).Replace("Ст3 6 mm", "AISI304 6 mm"), TestContext.Current.CancellationToken);
        var review = await f.ReviewAsync(); var candidate = Assert.Single(review.Candidates);
        Assert.Equal("Базовая", candidate.Draft!.MaterialGroup); Assert.Equal("Базовая", candidate.Baseline!.MaterialGroup);
        Assert.False(candidate.Draft.UsesMaterialCategories);
        Assert.Empty(candidate.Conflicts); Assert.True(candidate.Ready);
        Assert.Equal("100", candidate.Draft.PricePerMeter); Assert.Equal("AISI304", candidate.Draft.Material);
        await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error);
        Assert.Equal(1105, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].CuttingTotal);
    }

    [Fact]
    public async Task ChoosingAnotherReportPreservesManualPreviewEditsAndReconcilesItsSource()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        await ProjectFixture.WriteAsync(p);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(p.NativePath)!, "second.htm"), ProjectFixture.Html(p, 20000), TestContext.Current.CancellationToken);
        var review = await f.ReviewAsync(); var candidate = Assert.Single(review.Candidates);
        candidate.SelectedFile = candidate.Choices.Single(c => c.Name != "second.htm");
        candidate.Draft!.Coefficient = "1,5"; candidate.Draft.Length = "17";
        candidate.SelectedFile = candidate.Choices.Single(c => c.Name == "second.htm");
        Assert.Equal("1,5", candidate.Draft!.Coefficient); Assert.Equal("17", candidate.Draft.Length);
        var conflict = Assert.Single(candidate.Conflicts); Assert.Equal("Длина реза", conflict.Field); Assert.False(candidate.Ready);
        conflict.Selection = 0; Assert.True(candidate.Ready); Assert.Equal(2557.5m, candidate.Draft.Quote!.TotalCost);
    }

    [Fact]
    public async Task MigratedReportMustKeepItsSourceBindingAndCannotSilentlyChangeMachine()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        await ProjectFixture.WriteAsync(p); await f.AcceptAsync();
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var old = book with { SchemaVersion = 2, Orders = book.Orders.Select(o => o with { Items = [], Projects = [] }).ToArray() };
        await File.WriteAllTextAsync(Path.Combine(f.DirectoryPath, "workspace.json"), JsonSerializer.Serialize(old), TestContext.Current.CancellationToken);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        await File.WriteAllTextAsync(ProjectFixture.ReportPath(p), ProjectFixture.Html(p).Replace("--ЛАЗЕР", "--ПЛАЗМА"), TestContext.Current.CancellationToken);
        var review = await f.ReviewAsync(); Assert.Empty(Assert.Single(review.Candidates).Choices);
        Assert.Single((await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Specifications);
    }

    [Fact]
    public async Task AcceptedCopyVerificationDetectsLaterExternalModification()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        await ProjectFixture.WriteAsync(p); await f.AcceptAsync();
        var report = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0].Specifications[0].Current.Report!;
        await WorkflowReportFiles.VerifyArchiveAsync(report, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(report.ArchivePath, "Изменённая копия", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowReportFiles.VerifyArchiveAsync(report, TestContext.Current.CancellationToken));
    }
}
