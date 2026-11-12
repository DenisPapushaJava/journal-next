using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using PdfSharp.Drawing;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowAutomaticCalculationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("Лазер")] [InlineData("Плазма")] [InlineData("Газо-кислородная")]
    public async Task KnownReportsCalculateWithoutReviewAndRepeatDoesNotWrite(string machine)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(machine); await ProjectFixture.WriteAsync(project);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.Empty(review.GuidedCandidates); Assert.True(f.Model.CompleteCalculation(review), f.Model.CalculationNotice);
        f.Model.GenerateResultCommand.Execute(null); Assert.NotEmpty(f.Model.ResultText);
        var saved = (await f.Store.ReadAsync(Token)).Orders.Single(); var report = Assert.Single(saved.Specifications).Current.Report!;
        Assert.Equal(await File.ReadAllBytesAsync(ProjectFixture.ReportPath(project), Token), await File.ReadAllBytesAsync(report.ArchivePath, Token));
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
        review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.True(f.Model.CompleteCalculation(review), f.Model.CalculationNotice); Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
    }

    [Theory]
    [InlineData(2, 1, 1, "Ст-20", null, 2, true)]
    [InlineData(0, 1, 1, "Ст-20", null, 0, true)]
    [InlineData(10, 2, 1, "Ст-20", null, 20, true)]
    [InlineData(13, 2, 4, "Ст-20", "3", 35, true)]
    [InlineData(10, 1, 1, "Ст-20", "", null, false)]
    [InlineData(10, 1, 1, "Сталь 20/09Г2С", null, 10, false)]
    public async Task AwjcTotalsPunchesAcrossPartsAndDoesNotMultiplyBySheetCount(int punches, int quantity, int sheets,
        string grade, string? secondPartPunches, int? expectedPunches, bool automatic)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync("ГАР");
        WriteAwjc(project, punches, quantity, sheets, grade, secondPartPunches);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(project.NativePath!)!, "Карта_pp.PDF"), "Карта для цеха", Token);

        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        if (automatic)
        {
            Assert.Empty(review.GuidedCandidates);
            Assert.True(f.Model.CompleteCalculation(review), f.Model.CalculationNotice);
            var saved = Assert.Single((await f.Store.ReadAsync(Token)).Orders.Single().Specifications).Current;
            Assert.Equal(expectedPunches, saved.Parameters.PierceCount);
            Assert.Equal(grade, saved.Parameters.Material);
        }
        else
        {
            var candidate = Assert.Single(review.GuidedCandidates);
            Assert.Equal(expectedPunches, candidate.Snapshot!.Parsed.PunchCount?.Value);
            Assert.Equal(expectedPunches is not null, candidate.Draft!.MaterialNeedsReview);
            if (expectedPunches is null) Assert.Equal("1", candidate.Draft.Pierces);
            Assert.Empty((await f.Store.ReadAsync(Token)).Orders.Single().Specifications);
        }
    }

    [Fact]
    public async Task NewReportUpdatesAutomaticallyKeepingCoefficientOperationsAndSavedTariff()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        f.Model.SelectedDraft!.Coefficient = "1,2"; f.Model.SelectedDraft.HasChamfer = true; f.Model.SelectedDraft.Comment = "Пометка";
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null);
        var old = (await f.Store.ReadAsync(Token)).Orders.Single().Specifications.Single().Current;
        var prices = await f.Store.ReadAsync(Token);
        await f.Store.WriteAsync(prices.Version, b => WorkflowChanges.SetRate(b, b.Rates.Single(r => r.MachineId == project.MachineId) with { PricePerMeter = 999 }), Token);
        await f.Model.ReloadCommand.ExecuteAsync(null);
        await ProjectFixture.WriteAsync(project, 20000);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.True(f.Model.CompleteCalculation(review), f.Model.CalculationNotice);
        var current = (await f.Store.ReadAsync(Token)).Orders.Single().Specifications.Single().Current;
        Assert.Equal(21, current.Parameters.CutLengthMeters); Assert.Equal(1.2m, current.Parameters.ManualCoefficient);
        Assert.True(current.Parameters.HasChamfer); Assert.Equal("Пометка", current.Parameters.Comment);
        Assert.Equal(old.Calculation.PricePerMeter, current.Calculation.PricePerMeter);
    }

    [Fact]
    public async Task MissingReportKeepsReadyWorkAndNamesNextProjectWithoutInventingFullResult()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var ready = await f.AddAsync(); var missing = await f.AddAsync();
        await ProjectFixture.WriteAsync(ready);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.Empty(review.GuidedCandidates); Assert.False(f.Model.CompleteCalculation(review));
        Assert.Single((await f.Store.ReadAsync(Token)).Orders.Single().Specifications); Assert.Empty(f.Model.ResultText);
        Assert.Contains(missing.Name, f.Model.CalculationNotice); Assert.Contains("Готово 1 из 2", f.Model.CalculationNotice);
        Assert.Equal(CalculationTarget.Folder, f.Model.GoToCalculationProblem()); Assert.Equal(missing.Id, f.Model.SelectedProject!.Project.Id);
        await ProjectFixture.WriteAsync(missing); review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.True(f.Model.CompleteCalculation(review), f.Model.CalculationNotice);
    }

    [Fact]
    public async Task AmbiguityOnlyShowsQuestionAfterReadyProjectsAreSavedAndContinuationUsesNewOrderVersion()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var ready = await f.AddAsync(); var question = await f.AddAsync();
        await ProjectFixture.WriteAsync(ready); await ProjectFixture.WriteAsync(question);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(question.NativePath!)!, "Другой отчёт.html"), ProjectFixture.Html(question, 20000), Token);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.Single((await f.Store.ReadAsync(Token)).Orders.Single().Specifications);
        var candidate = Assert.Single(review.GuidedCandidates); Assert.Equal(question.Id, candidate.Project.Id); Assert.True(candidate.HasSourceChoice);
        candidate.SelectedFile = candidate.Choices.Single(c => c.Snapshot.SourcePath == ProjectFixture.ReportPath(question));
        Assert.True(review.SaveCommand.CanExecute(null)); await review.SaveCommand.ExecuteAsync(null); Assert.Null(review.Error); Assert.True(review.WasSaved);
        Assert.True(f.Model.CompleteCalculation(review), f.Model.CalculationNotice);
        Assert.Equal(2, (await f.Store.ReadAsync(Token)).Orders.Single().Specifications.Length);
    }

    [Fact]
    public async Task ChangedReportAutomaticallyReplacesManualFieldsAndArchivesThePreviousCalculation()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        f.Model.SelectedDraft!.Length = "15"; await f.Model.SaveSpecificationCommand.ExecuteAsync(null);
        await ProjectFixture.WriteAsync(project, 20000); var before = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.Empty(review.GuidedCandidates); Assert.Null(review.Error);
        Assert.NotEqual(before, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
        Assert.True(f.Model.CompleteCalculation(review), f.Model.CalculationNotice);
        Assert.Equal(21, (await f.Store.ReadAsync(Token)).Orders.Single().Specifications.Single().Current.Parameters.CutLengthMeters);
    }

    [AvaloniaFact]
    public async Task ExcludedDraftIsPreservedWhileIncludedProjectsRefreshAndGenerateResult()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var included = await f.AddAsync(); var excluded = await f.AddAsync();
        await ProjectFixture.WriteAsync(included); await ProjectFixture.WriteAsync(excluded); await f.AcceptAsync();
        await f.Model.SetProjectIncludedAsync(excluded.Id, false); var draft = f.Model.Drafts.Single(d => d.Id == excluded.Id); draft.Coefficient = "1,7";
        await ProjectFixture.WriteAsync(included, 20000);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.True(f.Model.CompleteCalculation(review), f.Model.CalculationNotice); Assert.True(f.Model.CanGenerateResult, f.Model.GenerateResultHint);
        Assert.Same(draft, f.Model.Drafts.Single(d => d.Id == excluded.Id)); Assert.Equal("1,7", draft.Coefficient); Assert.True(draft.IsDirty);
    }

    [Fact]
    public async Task ValidLocalCoefficientIsSavedDuringCalculationWithoutASeparateSave()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        f.Model.SelectedDraft!.Coefficient = "1,3";
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
        var calculated = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token)); Assert.Empty(calculated.GuidedCandidates);
        Assert.Equal("1,3", f.Model.SelectedDraft!.Coefficient); Assert.False(f.Model.SelectedDraft.IsDirty);
        Assert.NotEqual(before, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task PrimaryActionUpdatesResultAndKeepsProjectsUntilUserOpensResult(bool dark)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project);
        using var log = new WorkflowWindowTests.BindingLog();
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json")))
        { DataContext = f.Model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            Click(window.FindControl<Button>("RefreshWorkflowReports")!);
            await Until(() => f.Model.ResultText.Length > 0 && !f.Model.IsBusy);
            Assert.Equal(0, f.Model.OrderTabIndex);
            Assert.NotEmpty(f.Model.ResultText); Assert.Empty(window.OwnedWindows); Assert.False(f.Model.HasCalculationNotice);
            Frame(window, $"calculation-success-{dark}");
            var originalText = f.Model.ResultText;
            var tabChanges = 0;
            void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(WorkflowViewModel.OrderTabIndex)) tabChanges++; }
            f.Model.PropertyChanged += Changed;
            try
            {
                await ProjectFixture.WriteAsync(project, 20000); Click(window.FindControl<Button>("RefreshWorkflowReports")!);
                await Until(() => f.Model.ResultText != originalText && !f.Model.IsBusy);
                Assert.Equal("21", f.Model.SelectedDraft!.Length); Assert.Equal(project.Id, f.Model.SelectedProject!.Project.Id);
                Assert.False(f.Model.ResultIsStale); Assert.Equal(0, f.Model.OrderTabIndex); Assert.Equal(0, tabChanges);
                Click(window.FindControl<Button>("OpenWorkflowResult")!); Assert.Equal(1, f.Model.OrderTabIndex);
                f.Model.ResultText = "Мой текст"; f.Model.OrderTabIndex = 0; tabChanges = 0; Pump();
                Click(window.FindControl<Button>("RefreshWorkflowReports")!);
                await Until(() => f.Model.ResultStatus.Contains("Ручной текст сохранён") && !f.Model.IsBusy);
                Assert.Equal("Мой текст", f.Model.ResultText); Assert.Equal(0, f.Model.OrderTabIndex); Assert.Equal(0, tabChanges);
                Assert.Empty(window.OwnedWindows);
            }
            finally { f.Model.PropertyChanged -= Changed; }
            Assert.Empty(log.Errors);
        }
        finally { WorkflowTestWindows.Close(window); }
    }

    [AvaloniaTheory]
    [InlineData(false, 1)] [InlineData(true, 15)]
    public async Task AwjcShowsEstimateAndLetsKeepOrReplacePierces(bool dark, int expectedPierces)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync("ГАР"); WriteAwjc(project);
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json")))
        { DataContext = f.Model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Click(window.FindControl<Button>("RefreshWorkflowReports")!);
            await Until(() => window.OwnedWindows.OfType<WorkflowReportReviewWindow>().Any());
            var dialog = Assert.Single(window.OwnedWindows.OfType<WorkflowReportReviewWindow>());
            var review = Assert.IsType<WorkflowReportReviewViewModel>(dialog.DataContext); var candidate = Assert.Single(review.VisibleCandidates);
            Assert.False(candidate.Draft!.MaterialNeedsReview); Assert.False(candidate.HasSourceChoice);
            var estimated = Assert.IsType<CuttingCalculation>(candidate.Draft.Quote).TotalCost;
            Assert.False(dialog.FindControl<Expander>("ReviewReportFields")!.IsExpanded);
            var pierces = dialog.FindControl<TextBox>("ReviewMissingPierces")!;
            Assert.True(pierces.IsEffectivelyVisible);
            Assert.Equal("1", pierces.Text);
            Assert.Contains("Оставить 1", review.SaveCaption);
            if (expectedPierces != 1)
            {
                pierces.Text = FormattableString.Invariant($"{expectedPierces}"); Pump();
                Assert.Contains("Применить", review.SaveCaption);
                Assert.True(candidate.Draft.Quote!.TotalCost > estimated);
            }
            Frame(dialog, $"calculation-awjc-{dark}");
            var selection = review.Selected;
            Assert.Same(selection, review.Selected); Assert.True(review.SaveCommand.CanExecute(null));
            Click(dialog.FindControl<Button>("SaveWorkflowReports")!);
            await Until(() => window.OwnedWindows.Count == 0 && f.Model.ResultText.Length > 0);
            Assert.Equal(0, f.Model.OrderTabIndex);
            Assert.Contains("09Г2С", f.Model.ResultText);
            var saved = Assert.Single((await f.Store.ReadAsync(Token)).Orders.Single().Specifications).Current;
            Assert.Equal(expectedPierces, saved.Parameters.PierceCount);
            Assert.Equal(estimated + expectedPierces - 1, saved.Calculation.TotalCost);
            Assert.Equal("", saved.Report!.Parsed.Pierces);
            var repeated = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
            Assert.Empty(repeated.GuidedCandidates); Assert.True(f.Model.CompleteCalculation(repeated), f.Model.CalculationNotice);
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); WorkflowTestWindows.Close(window); }
    }

    [AvaloniaFact]
    public async Task CalculatingKeepsManualResultUntilUserExplicitlyApprovesReplacingIt()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        f.Model.ResultText = "Ручной результат"; await ProjectFixture.WriteAsync(project, 20000);
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json"))) { DataContext = f.Model, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); f.Model.OrderTabIndex = 0; Pump(); Click(window.FindControl<Button>("RefreshWorkflowReports")!);
            await Until(() => window.OwnedWindows.OfType<WorkflowResultReplaceWindow>().Any());
            var prompt = Assert.Single(window.OwnedWindows.OfType<WorkflowResultReplaceWindow>());
            Assert.Equal(21, (await f.Store.ReadAsync(Token)).Orders.Single().Specifications.Single().Current.Parameters.CutLengthMeters);
            prompt.FindControl<Button>("KeepWorkflowResult")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump(); await Until(() => window.OwnedWindows.Count == 0);
            Assert.Equal("Ручной результат", f.Model.ResultText); Assert.True(f.Model.ResultIsStale); Assert.Equal(0, f.Model.OrderTabIndex);
            Click(window.FindControl<Button>("RefreshWorkflowReports")!);
            await Until(() => window.OwnedWindows.OfType<WorkflowResultReplaceWindow>().Any());
            prompt = Assert.Single(window.OwnedWindows.OfType<WorkflowResultReplaceWindow>());
            prompt.FindControl<Button>("ReplaceWorkflowResult")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && f.Model.ResultText != "Ручной результат");
            Assert.False(f.Model.ResultIsStale); Assert.Equal(0, f.Model.OrderTabIndex);
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); WorkflowTestWindows.Close(window); }
    }

    [Fact]
    public async Task ChangedFileBeforeAutomaticAcceptanceRejectsBatchAndKeepsSourceDraft()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CreateReportReviewAsync(Token, calculation: true));
        review.IsGuided = true; await ProjectFixture.WriteAsync(project, 20000); await review.AcceptAutomaticAsync(Token);
        Assert.NotNull(review.Error); Assert.Contains("изменился", review.Error); Assert.Empty((await f.Store.ReadAsync(Token)).Orders.Single().Specifications);
        Assert.NotNull(Assert.Single(review.Candidates).Draft);
    }

    [Fact]
    public async Task PendingEditsInAnotherOrderAreAutomaticallyProtectedAndRecoveredWithoutBlockingNewWork()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project);
        var pending = await f.ReviewAsync(); pending.Candidates[0].Draft!.Coefficient = "1,3";
        var id = f.Model.SelectedOrder!.Order.Id;
        f.Model.NewOrderCommand.Execute(null); f.Model.NewManager = "Иванов"; f.Model.NewCustomer = "Второй заказчик";
        await f.Model.CreateOrderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var other = await f.AddAsync(); await ProjectFixture.WriteAsync(other);
        var calculated = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token)); Assert.Empty(calculated.GuidedCandidates);
        var protectedHistory = await WorkflowCalculationHistory.ReadAsync(Path.GetDirectoryName(project.NativePath!)!, id, project.Id, Token);
        Assert.Equal("1,3", Assert.Single(protectedHistory.Snapshots).Draft!.Coefficient);
        f.Model.SelectedOrder = f.Model.Orders.Single(r => r.Order.Id == id);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token)); Assert.Empty(review.GuidedCandidates);
        Assert.Equal(1.3m, (await f.Store.ReadAsync(Token)).Orders.Single(o => o.Id == id).Specifications.Single().Current.Parameters.ManualCoefficient);
    }

    [Theory]
    [InlineData(null, false)] [InlineData("0", true)]
    public async Task HansMissingCategoryAndParserErrorsRequireAnExplicitDecision(string? category, bool parserError)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync("HansLaser");
        var parsed = new Jornal.Parsing.Specifications.SpecificationParseResult("hans-pdf/v1",
            new(new(new(2026, 11, 2), "2026-11-02-123456", "1102-123456", Path.GetFileName(project.NativePath!)), []),
            new("HansLaser", []), new("Ст3", []), new(new(6, 3000, 1500), []), new(1, []), new(10000, []), new(48.2m, []), new(2, []), new(5, []), new(TimeSpan.FromMinutes(10), []),
            parserError ? [new(Jornal.Parsing.Specifications.SpecificationField.CutLength, "Conflict", "Два разных значения длины реза.")] : [])
            { IsRecognized = true, MaterialId = category is null ? null : new(category, []) };
        var candidate = new WorkflowReportCandidate(await f.Store.ReadAsync(Token), project, 1,
            [new(project.NativePath + ".pdf", "snapshot", [1], parsed)], "", () => { });
        Assert.False(candidate.CanAcceptAutomatically); Assert.True(candidate.NeedsDecision);
        if (category is null)
        {
            Assert.True(candidate.NeedsTariffCategory); Assert.Contains("категори", candidate.Message);
            candidate.Draft!.MaterialGroup = "Базовая"; Assert.True(candidate.Ready); Assert.False(candidate.NeedsTariffCategory);
        }
        else { Assert.True(candidate.Ready); Assert.Contains("Два разных", candidate.Message); }
    }

    [Fact]
    public async Task SourceChangeAfterAcceptanceStopsFinalResultAndBecomesAnExplainedReview()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        await ProjectFixture.WriteAsync(project, 20000);
        Assert.False(await f.Model.FinalizeCalculationAsync(review, Token)); Assert.Contains(project.Name, f.Model.CalculationNotice);
        Assert.Empty(f.Model.ResultText); Assert.True(Assert.Single(review.Candidates).NeedsAttention);
        Assert.Equal(11, (await f.Store.ReadAsync(Token)).Orders.Single().Specifications.Single().Current.Parameters.CutLengthMeters);
        review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.True(await f.Model.FinalizeCalculationAsync(review, Token), f.Model.CalculationNotice);
    }

    [Fact]
    public async Task ChangedRemoteVersionStopsFinalResultWithoutAnyWrite()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single();
        await f.Store.WriteOrderAsync(order.Id, order.Version, b => WorkflowChanges.UpdateOrderFromManagerName(b, order.Id, order.Version, book.Employees.Single(e => e.Id == order.ManagerId).Name, order.TechnologistId, "123", book.Employees[1].Id, DateTimeOffset.Now), Token);
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
        Assert.False(await f.Model.FinalizeCalculationAsync(review, Token)); Assert.Contains("обновились", f.Model.CalculationNotice);
        Assert.Equal(CalculationTarget.Recalculate, f.Model.GoToCalculationProblem()); Assert.True(f.Model.CanRetryCalculation); Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
    }

    [AvaloniaFact]
    public async Task RememberedUnchangedSourceIgnoresAnUnrelatedLockedFileWithoutAReviewOrAnotherSave()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var path = Path.Combine(Path.GetDirectoryName(project.NativePath!)!, "Занятый отчёт.html"); await File.WriteAllTextAsync(path, "locked", Token);
        using var lease = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json"))) { DataContext = f.Model, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); var before = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
            Click(window.FindControl<Button>("RefreshWorkflowReports")!);
            await Until(() => window.OwnedWindows.Count == 0 && f.Model.ResultText.Length > 0);
            Assert.Equal(0, f.Model.OrderTabIndex);
            Assert.NotEmpty(f.Model.ResultText); Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
        }
        finally { foreach (var dialog in window.OwnedWindows.ToArray()) dialog.Close(); WorkflowTestWindows.Close(window); }
    }

    internal static void WriteAwjc(CuttingProject project, int? punches = null, int quantity = 1,
        int sheets = 1, string grade = "09Г2С", string? secondPartPunches = null)
    {
        using var pdf = new PdfSharp.Pdf.PdfDocument(); var page = pdf.AddPage(); page.Width = XUnit.FromPoint(720); page.Height = XUnit.FromPoint(842);
        using (var g = XGraphics.FromPdfPage(page))
        {
            var font = WorkflowPdfFont.Create(10);
            void Text(string value, double x, double y) => g.DrawString(value, font, XBrushes.Black, new XPoint(x, y));
            Text("Имя файла", 20, 50); Text(Path.GetFileName(project.NativePath!), 160, 50);
            Text("Материал", 20, 90); Text("Сталь 6 mm " + grade, 160, 90);
            Text("Резка Материал", 20, 150);
            Text("Время резки", 20, 190); Text("00:10:00", 160, 190); Text("Общий раскрой", 310, 190); Text("1", 460, 190);
            Text("Длинна реза", 20, 220); Text("10000 mm", 160, 220); Text("Толщина", 360, 220); Text("6", 460, 220);
            Text("Раскрой: 1500 x 3000 mm", 20, 250);
            Text("Количество", 20, 280); Text("Общий вес", 360, 280); Text("48.2", 460, 280);
            Text(FormattableString.Invariant($"листов: {sheets}"), 20, 295);
        }
        if (punches is { } count)
        {
            var details = pdf.AddPage(); details.Width = XUnit.FromPoint(720); details.Height = XUnit.FromPoint(842);
            using var graphics = XGraphics.FromPdfPage(details);
            var font = WorkflowPdfFont.Create(10);
            void Text(string value, double x, double y) => graphics.DrawString(value, font, XBrushes.Black, new XPoint(x, y));
            Text("№", 80, 140); Text("Наименован", 230, 140); Text("Кол-во", 310, 140); Text("Кол-во", 370, 140);
            Text("Вес", 430, 140); Text("Качество", 480, 140); Text("ие", 255, 152); Text("пробивок", 366, 152); Text("резки", 490, 152);
            void Part(int index, double y)
            {
                Text(FormattableString.Invariant($"{index}"), 83, y); Text("д.1", 255, y);
                Text(FormattableString.Invariant($"{quantity}"), 325, y);
                Text(FormattableString.Invariant($"{count}"), 385, y);
                Text("22,4", 430, y); Text("Medium", 480, y);
            }
            Part(0, 185);
            if (secondPartPunches is not null)
            {
                Text("1", 83, 235);
                Text("д.2", 255, 235);
                Text("3", 325, 235);
                if (secondPartPunches.Length > 0) Text(secondPartPunches, 385, 235);
            }
            Text("ПРИМЕЧАНИЕ:", 70, 300);
        }
        pdf.Save(Path.Combine(Path.GetDirectoryName(project.NativePath!)!, "Спецификация.pdf"));
    }

    private static void Click(Control c) { Pump(); var top = TopLevel.GetTopLevel(c)!; var p = c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top)!.Value; top.MouseDown(p, MouseButton.Left); top.MouseUp(p, MouseButton.Left); Pump(); }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done) { for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(done()); }
    private static void Frame(Window w, string name) { if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is { } p) { Directory.CreateDirectory(p); using var frame = w.CaptureRenderedFrame(); frame?.Save(Path.Combine(p, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); } }
}
