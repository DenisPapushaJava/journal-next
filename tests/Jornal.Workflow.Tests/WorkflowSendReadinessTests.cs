using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowSendReadinessTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [AvaloniaFact]
    public async Task ExcludedDraftDoesNotBlockIncludedProjectOrLoseItsInputDuringHandoff()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var first = await f.AddAsync(); var excluded = await f.AddAsync();
        await ProjectFixture.WriteAsync(first); await ProjectFixture.WriteAsync(excluded); await f.AcceptAsync();
        f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        await f.Model.SaveShopRootAsync(null, Path.Combine(f.DirectoryPath, "shop"), Token);
        await f.Model.SetProjectIncludedAsync(excluded.Id, false);
        var draft = Assert.Single(f.Model.Drafts, d => d.Id == excluded.Id); draft.Coefficient = "1,7";
        ShopSigningTestData.WriteBlockMap(first, Path.Combine(Path.GetDirectoryName(first.NativePath!)!, "Карта.pdf"));
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(first.NativePath!)!, "Лист.cnc"), "UP", Token);
        Assert.True(f.Model.HasPendingEdits); Assert.True(f.Model.CanSendToWork, f.Model.SendToWorkHint);
        var preview = await f.Model.PrepareShopTransferAsync(_ => Task.CompletedTask);
        Assert.NotNull(preview); Assert.True(preview.TextCopied, preview.Error); Assert.Single(preview.Projects);
        Assert.Same(draft, Assert.Single(f.Model.Drafts, d => d.Id == excluded.Id)); Assert.Equal("1,7", draft.Coefficient); Assert.True(draft.IsDirty);
    }

    [Fact]
    public async Task PendingReportForExcludedProjectDoesNotBlockCurrentIncludedComposition()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var first = await f.AddAsync(); var excluded = await f.AddAsync();
        await ProjectFixture.WriteAsync(first); await ProjectFixture.WriteAsync(excluded); await f.AcceptAsync();
        f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        await ProjectFixture.WriteAsync(excluded, 20000); var review = await f.ReviewAsync();
        var pending = Assert.Single(review.Candidates, c => c.Project.Id == excluded.Id); Assert.True(pending.NeedsAttention);
        Assert.False(f.Model.CanSendToWork);
        await f.Model.SetProjectIncludedAsync(excluded.Id, false); Assert.Null(f.Model.Error);
        Assert.True(pending.NeedsAttention); Assert.True(f.Model.CanSendToWork, f.Model.SendToWorkHint);
        Assert.Equal("Готово 1 из 1", f.Model.CompletenessCaption);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAttemptShowsSpecificReasonAndDirectNextStepWithoutAnyIoOrDialog(bool dark)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json")))
        { DataContext = f.Model, Width = 1180, Height = 768, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var before = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
            var send = window.FindControl<Button>("CopyWorkflowCuttingTask")!;
            Assert.True(send.IsEffectivelyEnabled); Assert.False(f.Model.CanSendToWork);
            Click(send); Assert.True(f.Model.IsCuttingTaskProblemVisible); Assert.Empty(window.OwnedWindows); Assert.Null(f.Model.ShopTransfer);
            var reason = window.FindControl<TextBlock>("WorkflowCuttingTaskNotice")!;
            Assert.True(reason.IsEffectivelyVisible); Assert.Contains("БЛЦБ", reason.Text);
            Assert.Equal(before, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is { } output)
            {
                Directory.CreateDirectory(output);
                using var frame = window.CaptureRenderedFrame(); frame?.Save(Path.Combine(output, $"send-specific-reason-1180-{dark}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
            Click(window.FindControl<Button>("GoToWorkflowCuttingTaskProblem")!);
            Assert.True(window.FindControl<Expander>("OrderHeaderExpander")!.IsExpanded);
            f.Model.Blcb = "009337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
            var saved = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
            f.Model.CuttingTaskText += "Ручная пометка"; var edited = f.Model.CuttingTaskText;
            f.Model.SelectedDraft!.Coefficient = "1,5"; Click(send);
            Assert.True(reason.IsEffectivelyVisible); Assert.Equal(edited, f.Model.CuttingTaskText);
            Assert.Contains(project.Name, reason.Text); Assert.Contains("сохраните", reason.Text);
            Assert.Empty(window.OwnedWindows); Assert.Null(f.Model.ShopTransfer);
            Assert.Equal(saved, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
        }
        finally { WorkflowTestWindows.Close(window); }
    }

    private static void Click(Control control)
    {
        Pump(); var top = TopLevel.GetTopLevel(control)!;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)!.Value;
        top.MouseDown(point, MouseButton.Left); top.MouseUp(point, MouseButton.Left); Pump();
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(done()); }
}
