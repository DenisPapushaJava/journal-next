using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowUsabilityWindowTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task CompactWorkspaceAlignsPanelsAndNavigatesWithoutDiscardingDrafts(bool dark, int width)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var ready = await f.AddAsync(); await ProjectFixture.WriteAsync(ready); await f.AcceptAsync();
        for (var i = 0; i < 6; i++) await f.AddAsync();
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); prefs.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            f.Model.SelectedProject = f.Model.Projects.Single(p => p.Project.Id == ready.Id); Pump();
            var grid = window.FindControl<DataGrid>("WorkflowProjectsGrid")!;
            var calc = window.FindControl<Border>("WorkflowProjectCalculation")!;
            var total = window.FindControl<Border>("WorkflowOrderTotal")!;
            AssertAligned(grid, calc, total);
            var gridBounds = Bounds(grid, window); var calcBounds = Bounds(calc, window); var totalBounds = Bounds(total, window);
            Assert.True(gridBounds.Bottom < calcBounds.Top && calcBounds.Bottom < totalBounds.Top);
            var viewport = Bounds(window.FindControl<ScrollViewer>("WorkflowCalculationFields")!, window);
            foreach (var name in new[] { "WorkflowCalculationMaterial", "WorkflowHasChamfer", "WorkflowCalculationDetails" })
            {
                var bounds = Bounds(window.FindControl<Control>(name)!, window);
                Assert.True(bounds.Top >= viewport.Top && bounds.Bottom <= viewport.Bottom, name + " is clipped: " + bounds + " in " + viewport);
            }
            Assert.True(grid.GetVisualDescendants().OfType<DataGridRow>().Count(r => r.IsEffectivelyVisible && r.Bounds.Height > 0 && Bounds(r, window).Bottom <= gridBounds.Bottom) >= 4);
            Assert.All(f.Model.Projects, p => { Assert.DoesNotContain("вариант", p.Position); Assert.False(p.HasAlternatives); });
            Assert.Equal("Готово 1 из 7", f.Model.CompletenessCaption);
            var draft = f.Model.SelectedDraft!; draft.Coefficient = "1,4"; Pump();
            Assert.Equal("Готово 0 из 7", f.Model.CompletenessCaption); Assert.Equal(1, f.Model.ReviewProjectCount);
            var splitterHeight = window.FindControl<Grid>("WorkflowProjectWorkspace")!.RowDefinitions[2].Height;
            Click(window.FindControl<Button>("WorkflowWaitingProjects")!);
            Assert.Null(f.Model.SelectedDraft); Assert.Equal(6, f.Model.WaitingProjectCount);
            Assert.True(calc.Bounds.Height <= 88); Assert.True(grid.Bounds.Height > gridBounds.Height + 40);
            var first = f.Model.SelectedProject!.Project.Id;
            Click(window.FindControl<Button>("WorkflowWaitingProjects")!); Assert.NotEqual(first, f.Model.SelectedProject!.Project.Id);
            Assert.Same(draft, f.Model.Drafts.Single(d => d.Id == ready.Id)); Assert.Equal("1,4", draft.Coefficient);
            SaveFrame(window, $"compact-pending-{width}-{dark}");
            Click(window.FindControl<Button>("WorkflowReviewProjects")!);
            Assert.Same(draft, f.Model.SelectedDraft); Assert.Equal(ready.Id, f.Model.SelectedProject!.Project.Id);
            Assert.Equal(splitterHeight, window.FindControl<Grid>("WorkflowProjectWorkspace")!.RowDefinitions[2].Height);
            AssertAligned(grid, calc, total); SaveFrame(window, $"compact-ready-{width}-{dark}");
            f.Model.CancelDraftCommand.Execute(null); await f.Model.SetProjectIncludedAsync(first, false);
            Assert.Equal("Готово 1 из 6", f.Model.CompletenessCaption); Assert.Equal(5, f.Model.WaitingProjectCount);
            f.Model.SelectedProject = f.Model.Projects.Single(p => p.Project.Id == ready.Id);
            var variant = await f.AddAsync("Плазма", redo: true);
            Assert.Equal("Готово 0 из 6", f.Model.CompletenessCaption); Assert.Equal(1, f.Model.ReviewProjectCount);
            Assert.True(f.Model.Projects.Single(p => p.Project.Id == ready.Id).HasAlternatives);
            f.Model.GoToReviewProjectCommand.Execute(null); Assert.Equal(variant.Id, f.Model.SelectedProject!.Project.Id);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task PartialAcceptanceReturnsToOrderAndTariffSetupPreservesRemainingReview(bool dark)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var a = await f.AddAsync(); var b = await f.AddAsync("Плазма"); var missing = await f.AddAsync();
        await ProjectFixture.WriteAsync(a); await ProjectFixture.WriteAsync(b);
        var book = await f.Store.ReadAsync(Token);
        await f.Store.WriteAsync(book.Version, w => WorkflowChanges.UpdateCatalog(w, w.Machines, w.Rates.Where(r => r.MachineId != b.MachineId).ToArray()), Token);
        var prefs = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); prefs.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(prefs) { DataContext = f.Model, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var importing = await OpenReview(); var review = (WorkflowReportReviewViewModel)importing.DataContext!;
            review.Selected = review.Candidates.Single(c => c.Project.Id == b.Id);
            review.Selected.Draft!.Length = "15"; review.Selected.Draft.Coefficient = "1,3"; review.Selected.Draft.Comment = "Сохранить мой ввод";
            review.Selected.Draft.HasChamfer = true; Assert.False(review.Selected.Ready);
            await ProjectFixture.WriteAsync(b, 20000); await review.RefreshCommand.ExecuteAsync(null); Assert.Null(review.Error);
            var conflict = Assert.Single(review.Selected!.Conflicts); Assert.Equal(-1, conflict.Selection);
            Click(importing.FindControl<Button>("SaveWorkflowReports")!);
            await Until(() => window.OwnedWindows.Count == 0);
            Assert.True(review.WasSaved); Assert.Contains("Принято 1", f.Model.ReportImportNotice);
            Assert.True(f.Model.HasPendingEdits);
            Assert.Equal("Готово 1 из 3", f.Model.CompletenessCaption); Assert.Equal(1, f.Model.WaitingProjectCount); Assert.Equal(1, f.Model.ReviewProjectCount);
            importing = await OpenReview(); review = (WorkflowReportReviewViewModel)importing.DataContext!;
            review.Selected = review.Candidates.Single(c => c.Project.Id == b.Id);
            var candidate = review.Selected; var draft = candidate.Draft!; conflict = Assert.Single(candidate.Conflicts);
            Assert.Equal("15", draft.Length); Assert.Equal("1,3", draft.Coefficient); Assert.Equal("Сохранить мой ввод", draft.Comment); Assert.True(draft.HasChamfer);
            var source = candidate.SelectedFile;
            Click(importing.FindControl<Button>("ConfigureReviewPrices")!);
            await Until(() => importing.OwnedWindows.Count == 1);
            var catalogWindow = Assert.IsType<WorkflowCatalogWindow>(Assert.Single(importing.OwnedWindows));
            var catalog = (WorkflowCatalogViewModel)catalogWindow.DataContext!;
            Assert.Equal(b.MachineId, catalog.SelectedMachine!.Id); Assert.Equal("6", catalog.SelectedRate!.Thickness);
            catalog.SelectedRate.PricePerMeter = "200"; catalog.SelectedRate.PricePerPierce = "2";
            await catalog.SaveCommand.ExecuteAsync(null); Assert.Null(catalog.Error); catalogWindow.Close(); Pump();
            Assert.Same(candidate, review.Selected); Assert.Same(draft, candidate.Draft); Assert.Same(source, candidate.SelectedFile); Assert.Same(conflict, Assert.Single(candidate.Conflicts));
            Assert.Equal("200", draft.PricePerMeter); Assert.Equal("15", draft.Length); Assert.Equal("1,3", draft.Coefficient); Assert.True(draft.HasChamfer);
            Assert.Equal("100", f.Model.Drafts.Single(d => d.Id == a.Id).PricePerMeter);
            Assert.False(candidate.Ready); conflict.Selection = 0; Assert.True(candidate.Ready);
            SaveFrame(importing, $"review-tariff-{dark}");
            Click(importing.FindControl<Button>("SaveWorkflowReports")!); await Until(() => window.OwnedWindows.Count == 0);
            Assert.Equal("Готово 2 из 3", f.Model.CompletenessCaption); Assert.Contains("ещё 1", f.Model.ReportImportNotice);
            Assert.False(f.Model.HasPendingEdits);
            Click(window.FindControl<Button>("WorkflowWaitingProjects")!); Assert.Equal(missing.Id, f.Model.SelectedProject!.Project.Id);
            SaveFrame(window, $"accepted-notice-{dark}");

            async Task<WorkflowReportReviewWindow> OpenReview()
            {
                Click(window.FindControl<Button>("RefreshWorkflowReports")!);
                await Until(() => window.OwnedWindows.OfType<WorkflowReportReviewWindow>().Any());
                return Assert.Single(window.OwnedWindows.OfType<WorkflowReportReviewWindow>());
            }
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close(); window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedOrCancelledAcceptanceKeepsReviewOpen(bool cancel)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p);
        var source = await f.ReviewAsync();
        var review = new WorkflowReportReviewViewModel((_, _) => cancel ? throw new OperationCanceledException() : Task.FromResult<string?>("Ошибка записи"));
        review.Candidates.Add(source.Candidates[0]); review.Selected = source.Candidates[0]; review.Changed();
        var window = new WorkflowReportReviewWindow { DataContext = review };
        try
        {
            window.Show(); await review.SaveCommand.ExecuteAsync(null); Pump();
            Assert.True(window.IsVisible); Assert.False(review.WasSaved); Assert.NotNull(review.Error); Assert.False(review.IsBusy);
        }
        finally { window.Close(); }
    }

    private static Rect Bounds(Control control, Window window) => new(control.TranslatePoint(default, window)!.Value, control.Bounds.Size);
    private static void AssertAligned(params Control[] controls)
    {
        var window = (Window)TopLevel.GetTopLevel(controls[0])!; var expected = Bounds(controls[0], window);
        foreach (var control in controls.Skip(1))
        { var rect = Bounds(control, window); Assert.InRange(Math.Abs(expected.Left - rect.Left), 0, 1); Assert.InRange(Math.Abs(expected.Right - rect.Right), 0, 1); }
    }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!; var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left); root.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, Token); Pump(); }
        Pump(); Assert.True(done());
    }
    private static void SaveFrame(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
