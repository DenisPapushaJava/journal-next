using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowTaskTextFreshnessTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<CuttingProject> Ready(ProjectFixture f)
    {
        await f.InitializeAsync(); var p = await f.AddAsync(); await ProjectFixture.WriteAsync(p); await f.AcceptAsync();
        f.Model.Blcb = "9337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null); return p;
    }
    private static async Task Calculate(ProjectFixture f)
    {
        var review = Assert.IsType<WorkflowReportReviewViewModel>(await f.Model.CalculateOrderAsync(Token));
        Assert.Empty(review.GuidedCandidates); Assert.True(await f.Model.FinalizeCalculationAsync(review, Token), f.Model.CalculationNotice);
    }

    [AvaloniaTheory]
    [InlineData("coefficient")] [InlineData("price")] [InlineData("report")]
    public async Task CalculationOnlyChangesKeepManualTextAndAllowTheWholeHandoff(string change)
    {
        using var f = new ProjectFixture(); var p = await Ready(f); var text = f.Model.CuttingTaskText + "Дополнительная пометка";
        f.Model.CuttingTaskText = text;
        if (change == "coefficient") { f.Model.SelectedDraft!.Coefficient = "1,3"; await Calculate(f); }
        else if (change == "report") { await ProjectFixture.WriteAsync(p, 20000); await Calculate(f); }
        else
        {
            var book = await f.Store.ReadAsync(Token); var rate = book.Rates.Single(r => r.MachineId == p.MachineId);
            await f.Store.WriteAsync(book.Version, b => WorkflowChanges.SetRate(b, rate with { PricePerMeter = 150 }), Token);
            await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
            f.Model.RefreshRateCommand.Execute(null); await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        }
        Assert.False(f.Model.IsCuttingTaskTextStale); Assert.Equal(text, f.Model.CuttingTaskText);
        Assert.True(f.Model.CanSendToWork, f.Model.SendToWorkHint); Assert.False(f.Model.ConfirmCuttingTaskTextCommand.CanExecute(null));
        await f.Model.SaveShopRootAsync(null, Path.Combine(f.DirectoryPath, "shop"), Token);
        ShopSigningTestData.WriteBlockMap(p, Path.Combine(Path.GetDirectoryName(p.NativePath!)!, "Карта.pdf"));
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(p.NativePath!)!, "Лист.cnc"), "UP", Token);
        string? copied = null; var transfer = await f.Model.PrepareShopTransferAsync(value => { copied = value; return Task.CompletedTask; });
        Assert.NotNull(transfer); Assert.True(transfer.AllPrepared, transfer.Error); Assert.True(transfer.TextCopied, transfer.Error);
        Assert.Equal(text, copied); Assert.False(f.Model.IsCuttingTaskTextStale); Assert.True(f.Model.CanSendToWork, f.Model.SendToWorkHint);
    }

    [Fact]
    public async Task AnotherVariantWithTheSameLinesDoesNotRequireReviewOfManualText()
    {
        using var f = new ProjectFixture(); var first = await Ready(f); var text = f.Model.CuttingTaskText + "Ручная пометка"; f.Model.CuttingTaskText = text;
        var next = await f.AddAsync(redo: true); await ProjectFixture.WriteAsync(next, 20000); await f.AcceptAsync();
        f.Model.SelectedProject = f.Model.Projects.Single(p => p.Project.Id == next.Id);
        await f.Model.SelectProjectForTotalCommand.ExecuteAsync(null); Assert.Null(f.Model.Error); await Calculate(f);
        Assert.NotEqual(first.Id, f.Model.SelectedOrder!.Order.Items.Single().CurrentProjectId);
        Assert.Equal(text, f.Model.CuttingTaskText); Assert.False(f.Model.IsCuttingTaskTextStale); Assert.True(f.Model.CanSendToWork, f.Model.SendToWorkHint);
    }

    [Theory]
    [InlineData("blcb")] [InlineData("chamfer")] [InlineData("composition")]
    public async Task ActualMessageChangesKeepManualInputUntilConfirmedOrRegenerated(string change)
    {
        using var f = new ProjectFixture(); var p = await Ready(f);
        if (change == "composition") { var other = await f.AddAsync(); await ProjectFixture.WriteAsync(other); await f.AcceptAsync(); }
        var text = f.Model.CuttingTaskText + "Ручная пометка"; f.Model.CuttingTaskText = text;
        if (change == "blcb") { f.Model.Blcb = "9338"; await f.Model.SaveHeaderCommand.ExecuteAsync(null); }
        else if (change == "chamfer") { f.Model.SelectedDraft!.HasChamfer = true; await Calculate(f); }
        else await f.Model.SetProjectIncludedAsync(p.Id, false);
        Assert.True(f.Model.IsCuttingTaskTextStale); Assert.Equal(text, f.Model.CuttingTaskText); Assert.False(f.Model.CanSendToWork);
        var copied = false; await f.Model.CopyCuttingTaskAsync(_ => { copied = true; return Task.CompletedTask; }); Assert.False(copied);
        var before = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
        Assert.True(f.Model.ConfirmCuttingTaskTextCommand.CanExecute(null)); f.Model.ConfirmCuttingTaskTextCommand.Execute(null);
        Assert.False(f.Model.IsCuttingTaskTextStale); Assert.True(f.Model.CanSendToWork, f.Model.SendToWorkHint); Assert.Equal(text, f.Model.CuttingTaskText);
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
        f.Model.Blcb = "9999"; await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.True(f.Model.IsCuttingTaskTextStale);
        f.Model.ResetCuttingTaskTextCommand.Execute(null); Assert.False(f.Model.IsCuttingTaskTextStale); Assert.False(f.Model.HasCuttingTaskTextEdits);
        Assert.StartsWith("9999", f.Model.CuttingTaskText); Assert.DoesNotContain("Ручная пометка", f.Model.CuttingTaskText);
    }

    [Fact]
    public async Task AutomaticTextUpdatesWithoutReviewAndConfirmationCannotBypassUnfinishedCalculation()
    {
        using var f = new ProjectFixture(); await Ready(f); f.Model.Blcb = "9338"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        Assert.StartsWith("9338", f.Model.CuttingTaskText); Assert.False(f.Model.IsCuttingTaskTextStale); Assert.True(f.Model.CanSendToWork);
        f.Model.CuttingTaskText += "Пометка"; f.Model.Blcb = "9339"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        f.Model.SelectedDraft!.Coefficient = "1,3"; Assert.False(f.Model.CanConfirmCuttingTaskText);
        f.Model.ConfirmCuttingTaskTextCommand.Execute(null); f.Model.CancelDraftCommand.Execute(null);
        Assert.True(f.Model.IsCuttingTaskTextStale);
        f.Model.CuttingTaskText = ""; Assert.False(f.Model.CanConfirmCuttingTaskText); Assert.False(f.Model.CanSendToWork);
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExistingPopupOffersReviewOnlyForChangedLinesAndKeepsTextOnConfirmation(bool dark)
    {
        using var f = new ProjectFixture(); await Ready(f); using var log = new WorkflowWindowTests.BindingLog();
        var window = new RecordingWindow(new(Path.Combine(f.DirectoryPath, "preferences.json")))
        { DataContext = f.Model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = 1180, Height = 768 };
        try
        {
            window.Show();
            for (var i = 0; i < 500 && f.Model.IsBusy; i++) { await Task.Delay(10, Token); Pump(); }
            Assert.False(f.Model.IsBusy); Pump(); var text = f.Model.CuttingTaskText.Replace("Ст3", "Ст-3", StringComparison.Ordinal); f.Model.CuttingTaskText = text;
            var toggle = window.FindControl<Button>("ToggleWorkflowCuttingTaskPreview")!; toggle.Flyout!.ShowAt(toggle); Pump();
            var review = window.FindControl<Grid>("WorkflowTaskTextReview")!; var confirm = window.FindControl<Button>("ConfirmWorkflowTaskText")!;
            Assert.False(review.IsEffectivelyVisible);
            f.Model.Blcb = "9338"; await f.Model.SaveHeaderCommand.ExecuteAsync(null); Pump();
            Assert.True(review.IsEffectivelyVisible); Assert.True(confirm.IsEffectivelyEnabled); Assert.False(window.FindControl<Button>("CopyWorkflowTaskPreviewText")!.IsEffectivelyEnabled);
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is { } output)
            { Directory.CreateDirectory(output); using var frame = TopLevel.GetTopLevel(review)!.CaptureRenderedFrame(); frame?.Save(Path.Combine(output, $"task-text-review-{dark}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            Click(confirm);
            Assert.False(review.IsEffectivelyVisible); Assert.Equal(text, f.Model.CuttingTaskText); Assert.True(window.FindControl<Button>("CopyWorkflowTaskPreviewText")!.IsEffectivelyEnabled);
            Assert.True(f.Model.CanSendToWork);
            Click(window.FindControl<Button>("CopyWorkflowTaskPreviewText")!);
            for (var i = 0; i < 500 && window.Copied is null; i++) { await Task.Delay(10, Token); Pump(); }
            Assert.Equal(text, window.Copied); Assert.DoesNotContain("проверен", window.Copied); Assert.DoesNotContain("Сформировать заново", window.Copied);
            Assert.Empty(log.Errors);
        }
        finally { WorkflowTestWindows.Close(window); }
    }
    private sealed class RecordingWindow(WorkflowPreferencesStore preferences) : WorkflowWindow(preferences)
    {
        internal string? Copied;
        protected override Task WriteCuttingTaskClipboardAsync(string text) { Copied = text; return Task.CompletedTask; }
    }
    private static void Click(Control control)
    {
        var top = TopLevel.GetTopLevel(control)!; var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
}
