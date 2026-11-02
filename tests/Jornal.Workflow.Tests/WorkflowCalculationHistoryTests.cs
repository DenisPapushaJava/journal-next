using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowCalculationHistoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static Task<CalculationHistoryRead> Read(ProjectFixture f, CuttingProject p) =>
        WorkflowCalculationHistory.ReadAsync(Path.GetDirectoryName(p.NativePath!)!, f.Model.SelectedOrder!.Order.Id, p.Id, Token);
    private static async Task<WorkflowReportReviewViewModel> Calculate(ProjectFixture f)
    {
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.Empty(review.GuidedCandidates); Assert.Null(review.Error); Assert.True(await f.Model.FinalizeCalculationAsync(review, Token), f.Model.CalculationNotice);
        return review;
    }
    [Fact]
    public async Task ChangedReportReplacesManualReportFieldsAndKeepsUsefulSettingsWithVisibleHistory()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        f.Model.SelectedDraft!.Length = "15"; f.Model.SelectedDraft.Coefficient = "1,3"; f.Model.SelectedDraft.HasChamfer = true; f.Model.SelectedDraft.Comment = "Пометка";
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var before = (await f.Store.ReadAsync(Token)).Orders.Single().Specifications.Single().Current;
        await ProjectFixture.WriteAsync(p, 20000); await Calculate(f);
        var current = (await f.Store.ReadAsync(Token)).Orders.Single().Specifications.Single().Current;
        Assert.Equal(21, current.Parameters.CutLengthMeters); Assert.Equal(1.3m, current.Parameters.ManualCoefficient); Assert.True(current.Parameters.HasChamfer);
        Assert.Equal("Пометка", current.Parameters.Comment); Assert.Equal(before.Calculation.PricePerMeter, current.Calculation.PricePerMeter);
        var history = Assert.IsType<WorkflowCalculationHistoryViewModel>(await f.Model.CreateCalculationHistoryAsync(p.Id, Token));
        Assert.Equal(2, history.Changes.Length); Assert.Contains(history.Changes[0].Fields, field => field.Field == "Длина реза, м" && field.Before == "15" && field.After == "21");
        Assert.Equal(before.Report!.Sha256, history.Changes[0].Report!.Sha256);
        await WorkflowReportFiles.VerifyArchiveAsync(history.Changes[0].Report!, Token);
        var bytes = await File.ReadAllBytesAsync(f.Store.FilePath, Token); var count = (await Read(f, p)).Snapshots.Length;
        await Calculate(f); Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Store.FilePath, Token)); Assert.Equal(count, (await Read(f, p)).Snapshots.Length);
    }

    [Fact]
    public async Task UnsavedInputIsPreservedBeforeIncomingNumbersReplaceItWithoutAnExtraDialog()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        f.Model.SelectedDraft!.Length = "15"; f.Model.SelectedDraft.Coefficient = "1,2";
        await ProjectFixture.WriteAsync(p, 20000); await Calculate(f);
        Assert.Equal("21", f.Model.SelectedDraft!.Length); Assert.Equal("1,2", f.Model.SelectedDraft.Coefficient); Assert.False(f.Model.SelectedDraft.IsDirty);
        var raw = Assert.Single((await Read(f, p)).Snapshots, s => s.Draft is not null).Draft!;
        Assert.Equal("15", raw.Values.Length); Assert.Equal("1,2", raw.Coefficient);
        var history = Assert.IsType<WorkflowCalculationHistoryViewModel>(await f.Model.CreateCalculationHistoryAsync(p.Id, Token));
        Assert.Contains(history.Changes, c => c.Caption.Contains("Ручные") && c.Fields.Any(d => d.Before == "15" && d.After == "21"));
    }

    [Fact]
    public async Task OwnHeaderSaveAndOldUnchangedPreviewDoNotRequireF5()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        await Calculate(f); // Retained unchanged preview used to block the next calculation after any order save.
        f.Model.Blcb = "123"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        await ProjectFixture.WriteAsync(p, 20000); await Calculate(f);
        Assert.Equal("21", f.Model.SelectedDraft!.Length); Assert.DoesNotContain("F5", f.Model.CalculationNotice);
    }

    [Fact]
    public async Task RememberedSourceIsUpdatedWithoutAskingAboutAnAdditionalOldReport()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(p.NativePath!)!, "Старый вариант.html"), ProjectFixture.Html(p, 30000), Token);
        await ProjectFixture.WriteAsync(p, 20000); await Calculate(f);
        Assert.Equal("21", f.Model.SelectedDraft!.Length);
    }

    [Fact]
    public async Task LatestColleagueSettingsSurviveARecalculationWithLocalReportFieldEdits()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        f.Model.SelectedDraft!.Length = "15";
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single(); var old = order.Specifications.Single().Current;
        await f.Store.WriteOrderAsync(order.Id, order.Version, b => WorkflowChanges.SaveSpecification(b, order.Id, order.Version, p.Id,
            old.Parameters with { ManualCoefficient = 1.7m }, old.DocumentNumber, old.DocumentDate, book.Employees[0].Id, DateTimeOffset.UtcNow,
            old.SourcePath, old.ImportProfile, report: old.Report), Token);
        await ProjectFixture.WriteAsync(p, 20000); await Calculate(f);
        var current = (await f.Store.ReadAsync(Token)).Orders.Single().Specifications.Single().Current;
        Assert.Equal(21, current.Parameters.CutLengthMeters); Assert.Equal(1.7m, current.Parameters.ManualCoefficient);
        Assert.Contains((await Read(f, p)).Snapshots, s => s.Draft is null && s.State!.Parameters.ManualCoefficient == 1.7m);
    }

    [Fact]
    public async Task HistoryFailureDoesNotReplaceCurrentCalculation()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(p.NativePath!)!, WorkflowCalculationHistory.FolderName), "blocked", Token);
        await ProjectFixture.WriteAsync(p, 20000);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.NotNull(review.Error); Assert.Contains("предыдущий расчёт", review.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
    }

    [Fact]
    public async Task OneDamagedSnapshotDoesNotHideTheRestAndHistoryReadsDoNotWrite()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        await ProjectFixture.WriteAsync(p, 20000); await Calculate(f);
        var folder = Path.Combine(Path.GetDirectoryName(p.NativePath!)!, WorkflowCalculationHistory.FolderName, p.Id.ToString("N"));
        await File.WriteAllTextAsync(Path.Combine(folder, "damaged.json"), "{broken", Token);
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, Token); var history = await Read(f, p);
        Assert.Single(history.Snapshots); Assert.Contains("1", history.Warning);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
    }

    [Fact]
    public async Task GuidedContinuationRebasesAColleagueChangeWithoutF5OrLosingInput()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync("ГАР"); WorkflowAutomaticCalculationTests.WriteAwjc(p);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        var candidate = Assert.Single(review.GuidedCandidates); candidate.Draft!.Pierces = "15"; candidate.Draft.Coefficient = "1,3";
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single();
        await f.Store.WriteOrderAsync(order.Id, order.Version, b => WorkflowChanges.UpdateOrderFromManagerName(b, order.Id, order.Version,
            book.Employees.Single(e => e.Id == order.ManagerId).Name, order.TechnologistId, "123", book.Employees[1].Id, DateTimeOffset.Now), Token);
        await review.SaveCommand.ExecuteAsync(null);
        Assert.Null(review.Error); Assert.True(review.WasSaved); Assert.True(await f.Model.FinalizeCalculationAsync(review, Token), f.Model.CalculationNotice);
        var saved = (await f.Store.ReadAsync(Token)).Orders.Single(); Assert.Equal("123", saved.Blcb);
        Assert.Equal(15, saved.Specifications.Single().Current.Parameters.PierceCount); Assert.Equal(1.3m, saved.Specifications.Single().Current.Parameters.ManualCoefficient);
    }

    [Fact]
    public async Task AlreadyAcceptedNewSourceDoesNotRestoreOldLocalNumericOverrides()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        var local = f.Model.SelectedDraft!; local.Length = "15"; local.Coefficient = "1,3";
        await ProjectFixture.WriteAsync(p, 20000);
        var snapshot = await WorkflowReportFiles.ReadAsync(ProjectFixture.ReportPath(p), Token);
        var book = await f.Store.ReadAsync(Token); var old = book.Orders.Single().Specifications.Single().Current;
        var archive = await WorkflowReportFiles.ArchiveAsync(snapshot, old.Report!.Parsed, Token);
        var order = book.Orders.Single();
        await f.Store.WriteOrderAsync(order.Id, order.Version, b => WorkflowChanges.SaveSpecification(b, order.Id, order.Version, p.Id,
            old.Parameters with { CutLengthMeters = 21 }, old.DocumentNumber, old.DocumentDate, book.Employees[0].Id, DateTimeOffset.UtcNow,
            old.SourcePath, old.ImportProfile, report: archive), Token);
        await Calculate(f); Assert.Equal("21", f.Model.SelectedDraft!.Length); Assert.Equal("1,3", f.Model.SelectedDraft.Coefficient);
    }

    [Fact]
    public async Task PreservedButNotReplacedStateIsNotShownAsACompletedChange()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single(); var current = order.Specifications.Single().Current;
        await WorkflowCalculationHistory.SaveAsync(Path.GetDirectoryName(p.NativePath!)!, new(1, order.Id, p.Id, DateTimeOffset.UtcNow,
            book.Employees[0].Id, current, null) { OrderVersion = order.Version }, Token);
        var history = Assert.IsType<WorkflowCalculationHistoryViewModel>(await f.Model.CreateCalculationHistoryAsync(p.Id, Token)); Assert.True(history.IsEmpty);
    }

    [AvaloniaFact]
    public async Task UnchangedCalculationPreservesManualResultWithoutAnotherConfirmation()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        f.Model.GenerateResultCommand.Execute(null); f.Model.ResultText = "Ручной результат";
        var review = await Calculate(f); Assert.True(f.Model.KeepCurrentManualResult()); Assert.Equal("Ручной результат", f.Model.ResultText);
    }

    [Fact]
    public async Task AChangedSpecificationRequiresItsOwnMaterialConfirmationAfterRefresh()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync("ГАР"); WorkflowAutomaticCalculationTests.WriteAwjc(p);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.Single(review.GuidedCandidates).Draft!.Pierces = "15";
        var path = Path.Combine(Path.GetDirectoryName(p.NativePath!)!, "Спецификация.pdf");
        using (var pdf = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
        { pdf.Info.Title = "Исправленная спецификация"; pdf.Save(path); }
        review.MarkOrderChanged(); await review.SaveCommand.ExecuteAsync(null);
        Assert.Null(review.Error); Assert.False(review.WasSaved); var candidate = Assert.Single(review.GuidedCandidates);
        Assert.True(candidate.Draft!.MaterialNeedsReview); Assert.False(candidate.Draft.MaterialConfirmed); Assert.True(candidate.NeedsPierceCount);
        Assert.Empty((await f.Store.ReadAsync(Token)).Orders.Single().Specifications);
    }

    [AvaloniaFact]
    public async Task ACorruptedHistoricalReportIsNeverOpened()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        await ProjectFixture.WriteAsync(p, 20000); await Calculate(f);
        var history = Assert.IsType<WorkflowCalculationHistoryViewModel>(await f.Model.CreateCalculationHistoryAsync(p.Id, Token));
        await File.WriteAllTextAsync(history.Selected!.Report!.ArchivePath, "Повреждённая копия", Token);
        var window = new HistoryWindow { DataContext = history };
        try
        {
            window.Show(); Pump(); window.FindControl<Button>("OpenHistoricalSpecification")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 500 && history.Error is null; i++) { await Task.Delay(10, Token); Pump(); }
            Assert.Contains("повреждена", history.Error); Assert.Null(window.OpenedPath);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task UnknownWriteOutcomeAfterAKnownConflictIsNotTreatedAsAnotherRetry()
    {
        var proxy = System.Reflection.DispatchProxy.Create<IWorkflowWorkspaceStore, WorkflowReviewRegressionTests.BeforeWriteStore>();
        var intercept = (WorkflowReviewRegressionTests.BeforeWriteStore)(object)proxy;
        using var f = new ProjectFixture(store => { intercept.Inner = store; return new(proxy, new(new WorkflowAppUpdater())); });
        await f.InitializeAsync(); var p = await f.AddAsync("ГАР"); WorkflowAutomaticCalculationTests.WriteAwjc(p);
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.Single(review.GuidedCandidates).Draft!.Pierces = "15";
        var book = await f.Store.ReadAsync(Token); var order = book.Orders.Single();
        await f.Store.WriteOrderAsync(order.Id, order.Version, b => WorkflowChanges.UpdateOrderFromManagerName(b, order.Id, order.Version,
            book.Employees.Single(e => e.Id == order.ManagerId).Name, order.TechnologistId, "123", book.Employees[1].Id, DateTimeOffset.Now), Token);
        var completedWrites = 0;
        intercept.After = _ => { completedWrites++; throw new WorkflowOutcomeUnknownException(); };
        await review.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, completedWrites); Assert.Contains("автоматически повторять", review.Error); Assert.False(review.WasSaved);
        Assert.False(f.Model.CanRetryCalculation); Assert.NotEqual("Продолжить", review.SaveCaption);
        Assert.Single((await f.Store.ReadAsync(Token)).Orders.Single().Specifications);
    }

    private sealed class HistoryWindow : WorkflowCalculationHistoryWindow
    {
        public string? OpenedPath;
        protected override Task<bool> LaunchReportAsync(string path) { OpenedPath = path; return Task.FromResult(true); }
    }
    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task HistoryClearlyShowsChangesAndOpensTheArchivedSpecification(bool dark)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await Calculate(f);
        var before = (await f.Store.ReadAsync(Token)).Orders.Single().Specifications.Single().Current;
        await ProjectFixture.WriteAsync(p, 20000); await Calculate(f);
        using var log = new WorkflowWindowTests.BindingLog();
        var history = Assert.IsType<WorkflowCalculationHistoryViewModel>(await f.Model.CreateCalculationHistoryAsync(p.Id, Token));
        var window = new HistoryWindow { DataContext = history, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = 960, Height = 620 };
        try
        {
            window.Show(); Pump(); Assert.Single(history.Changes); Assert.True(history.Selected!.CanOpenReport);
            var button = window.FindControl<Button>("OpenHistoricalSpecification")!;
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            for (var i = 0; i < 500 && window.OpenedPath is null; i++) { await Task.Delay(10, Token); Pump(); }
            Assert.Equal(before.Report!.ArchivePath, window.OpenedPath); Assert.NotEqual(ProjectFixture.ReportPath(p), window.OpenedPath);
            foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
                Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 1, $"Overflow {scroll.Extent} / {scroll.Viewport}");
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is { } output)
            { Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(output, $"calculation-history-{dark}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            foreach (var text in window.FindControl<DataGrid>("CalculationHistoryFields")!.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Text == before.Report.FileName))
            { Assert.Equal(0, text.MaxLines); Assert.DoesNotContain(text.TextLayout.TextLines, line => line.HasCollapsed); }
            Assert.Empty(log.Errors);
        }
        finally { window.Close(); }
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
}
