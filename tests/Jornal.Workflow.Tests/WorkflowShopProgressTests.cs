using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Jornal.Workflow.Core;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowShopProgressTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Capture : IProgress<WorkflowShopProgress>
    {
        public List<WorkflowShopProgress> Values { get; } = [];
        public void Report(WorkflowShopProgress value) => Values.Add(value);
    }

    [Fact]
    public async Task EventsDescribeRealSignedCopiedAndSkippedFilesIncludingBending()
    {
        using var f = new ShopFixture("HansLaser", bending: true);
        await File.WriteAllTextAsync(Path.Combine(f.Source, "ГИБКА", "Эскиз.txt"), "bending", Token);
        var events = new Capture(); var editor = new ShopWordEditor();
        var plan = Assert.Single(await WorkflowShopDiscovery.DiscoverAsync(f.ShopRoot, f.Order, f.Book, Token, editor, events));
        Assert.Contains(events.Values, e => e.File == "Карта.DOC" && e.Stage == WorkflowShopStage.Discovery);
        Assert.Contains(events.Values, e => e.File == "ГИБКА/Эскиз.txt");
        var selected = ShopFixture.Choose(plan);
        var signed = await WorkflowMapSigning.SignSelectedAsync(f.Order, f.Book, [selected], _ => Task.CompletedTask, Token, editor, details: events);
        Assert.All(signed, r => Assert.NotNull(r.VerifiedFile));
        var caption = WorkflowProjectComment.Format(f.Order, f.Project, f.Book.Employees, f.Book.Customers);
        Assert.All(signed, r => { Assert.Equal(caption, r.Caption); Assert.True(r.HasCaption); });
        Assert.Contains(events.Values, e => e.Stage == WorkflowShopStage.Signing && e.Completed == 0 && e.Total == 1);
        Assert.Contains(events.Values, e => e.Stage == WorkflowShopStage.Signing && e.Completed == 1 && e.Total == 1);
        var files = selected.Files.Select(file => signed.FirstOrDefault(s => s.VerifiedFile!.SourcePath == file.SourcePath)?.VerifiedFile ?? file).ToArray();
        selected = selected with { Project = plan with { Files = files }, Files = files };
        events.Values.Clear();
        var copied = Assert.Single(await WorkflowShopTransfer.CopyAsync(f.ShopRoot, "test-db", f.Order.Id, [selected], false,
            _ => Task.CompletedTask, token: Token, word: editor, details: events));
        Assert.True(copied.Success, copied.Message);
        Assert.Contains(events.Values, e => e.Stage == WorkflowShopStage.Copying && e.File == "Лист.GNC" && e.Completed < e.Total);
        Assert.Contains(events.Values, e => e.Stage == WorkflowShopStage.Copying && e.Completed == 3 && e.Total == 3);
        Assert.Contains(events.Values, e => e.Stage == WorkflowShopStage.Verifying && e.File == "Карта.DOC");
        Assert.All(events.Values.Where(e => e.Completed is not null), e => Assert.InRange(e.Completed!.Value, 0, e.Total!.Value));
        events.Values.Clear();
        var repeated = Assert.Single(await WorkflowShopTransfer.CopyAsync(f.ShopRoot, "test-db", f.Order.Id,
            [selected with { DestinationRevision = copied.DestinationRevision }], false, _ => Task.CompletedTask, token: Token, word: editor, details: events));
        Assert.True(repeated.Unchanged);
        Assert.Contains(events.Values, e => e.Activity == "Комплект уже актуален" && e.Completed == 3 && e.Total == 3);
        Assert.DoesNotContain(events.Values, e => e.Activity == "Копируем и проверяем файл");
    }

    [AvaloniaFact]
    public async Task ManyReportsHaveABoundedUiQueueAndDisposalDropsLateEvents()
    {
        var count = 0; WorkflowShopProgress? shown = null;
        using var feed = new WorkflowShopProgressFeed(value => { shown = value; count++; });
        for (var i = 0; i < 100_000; i++) feed.Report(new(WorkflowShopStage.Copying, File: i.ToString()));
        await Until(() => shown is not null);
        Assert.Equal("99999", shown!.File); Assert.Equal(1, count);
        feed.Report(new(WorkflowShopStage.Copying, File: "late")); feed.Dispose();
        await Task.Delay(250, Token); Pump(); Assert.Equal(1, count); Assert.Equal("99999", shown.File);
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ImmediateFailureKeepsTheActualStageAndLateEventsCannotReplaceTheOutcome(bool failCopy)
    {
        using var f = new ShopFixture("HansLaser"); var editorEntered = false; var copied = false;
        var editor = new ImmediateEditor(() =>
        {
            editorEntered = true;
            if (!failCopy) throw new WordMapUnavailableException("Microsoft Word недоступен. Карта не изменена.");
            return new(null, 1, 0);
        });
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "test", f.Order, f.Book,
            _ =>
            {
                if (failCopy && editorEntered) throw new WorkflowValidationException("Тест: ошибка перед копированием.");
                return Task.CompletedTask;
            }, _ => { copied = true; return Task.CompletedTask; }, wordMapEditor: editor);
        await model.RefreshAsync(); Assert.True(model.CanStartAutomatically, model.Error);
        await model.CopyAsync(); Assert.True(model.HasError); Assert.True(editorEntered);
        var expected = failCopy ? WorkflowShopStage.Copying : WorkflowShopStage.Signing;
        Assert.Equal(expected, model.Progress!.Stage);
        Assert.True(model.ProgressSteps[failCopy ? 2 : 1].IsCurrent);
        Assert.Contains(failCopy ? "перед копированием" : "Word", model.Error);
        Assert.False(copied); Assert.False(Directory.Exists(f.ShopRoot)); Assert.False(model.IsProgressSuccessful);
        var error = model.Error; var status = model.Status;
        await Task.Delay(250, Token); Pump();
        Assert.Equal(expected, model.Progress.Stage); Assert.Equal(error, model.Error); Assert.Equal(status, model.Status);
    }

    [AvaloniaFact]
    public async Task DelayedSigningEventCannotRollBackCopyingWhileDatabaseValidationIsPending()
    {
        using var f = new ShopFixture("HansLaser"); var editorEntered = false;
        var copyingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new ImmediateEditor(() => { editorEntered = true; return new(null, 1, 0); });
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "test", f.Order, f.Book,
            async token =>
            {
                if (editorEntered && !release.Task.IsCompleted)
                { copyingEntered.TrySetResult(); await release.Task.WaitAsync(token); }
            }, _ => Task.CompletedTask, wordMapEditor: editor);
        await model.RefreshAsync(); Assert.True(model.CanStartAutomatically, model.Error);
        var operation = model.CopyAsync();
        try
        {
            await copyingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(WorkflowShopStage.Copying, model.Progress!.Stage);
            // No new file event is emitted while validation waits. The queued Signing event must be ignored.
            await Task.Delay(250, Token); Pump();
            Assert.True(model.IsBusy); Assert.Equal(WorkflowShopStage.Copying, model.Progress.Stage);
        }
        finally { release.TrySetResult(); await operation; }
        Assert.True(model.IsProgressSuccessful, model.Error); Assert.Equal(WorkflowShopStage.Clipboard, model.Progress!.Stage);
    }

    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1480)] [InlineData(true, 1480)]
    public async Task SendShowsProgressImmediatelyAndKeepsTheCompletedSummaryUntilAcknowledged(bool dark, int width)
    {
        using var log = new WorkflowWindowTests.BindingLog(); var editor = new GateEditor();
        using var f = new ProjectFixture(wordMapEditor: editor); await WorkflowShopAutomationWindowTests.ReadyHans(f);
        var source = Path.GetDirectoryName(f.Model.Projects.Single().Project.NativePath!)!;
        var longName = "0-" + new string('Л', 110) + ".DOC";
        File.Move(Path.Combine(source, "Дополнительная.Gnf.DOC"), Path.Combine(source, longName));
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Update(p => p with { DarkTheme = dark });
        var window = new RecordingWindow(preferences) { DataContext = f.Model, Width = width, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            Click(window.FindControl<Button>("CopyWorkflowCuttingTask")!);
            Assert.Single(window.OwnedWindows.OfType<WorkflowShopProgressWindow>());
            await editor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Until(() => window.OwnedWindows.OfType<WorkflowShopProgressWindow>().Any());
            var progress = Assert.Single(window.OwnedWindows.OfType<WorkflowShopProgressWindow>());
            var model = Assert.IsType<WorkflowShopTransferViewModel>(progress.DataContext);
            await Until(() => model.Progress?.Stage == WorkflowShopStage.Signing && model.ProgressFile == longName);
            Assert.Equal(longName, model.ProgressFile); Assert.Equal(0, model.Progress!.Completed); Assert.Equal(2, model.Progress.Total);
            Assert.True(model.ProgressSteps[1].IsCurrent); Assert.True(model.ProgressSteps[0].IsDone);
            Assert.True(progress.FindControl<ProgressBar>("ShopProgressBar")!.IsIndeterminate);
            Assert.Equal(380, progress.Height); Assert.Equal(560, progress.Width);
            Assert.DoesNotContain(progress.GetVisualDescendants().OfType<ScrollViewer>(), s => s.IsEffectivelyVisible && s.Extent.Width > s.Viewport.Width + 1);
            Save(progress, $"shop-progress-signing-{width}-{dark}");
            editor.Release.SetResult();
            await Until(() => !f.Model.IsBusy && progress.FindControl<Button>("ShopProgressClose")!.IsVisible);
            Assert.True(model.AllPrepared); Assert.True(model.TextCopied); Assert.NotNull(window.Copied); Assert.Null(model.Error);
            Assert.True(progress.IsVisible); Assert.True(model.IsProgressSuccessful); Assert.Equal("Передано в цех", model.ProgressTitle);
            Assert.All(model.ProgressSteps, step => { Assert.True(step.IsDone); Assert.False(step.IsCurrent); });
            Assert.False(progress.FindControl<ProgressBar>("ShopProgressBar")!.IsVisible);
            Assert.Equal("Карты: обновлено файлов 2, без изменений 0.", model.MapSigningSummary);
            Assert.Empty(model.MapDetails);
            Save(progress, $"shop-progress-completed-{width}-{dark}");
            var editorCalls = editor.Calls;
            Directory.Move(source, source + "-unavailable"); // Details must not reopen the maps or revalidate the project.
            Click(progress.FindControl<Button>("ShopMapDetailsToggle")!);
            await Until(() => model.MapDetailsExpanded);
            Assert.Equal(580, progress.Height); Assert.Equal(2, model.MapDetails.Count);
            Assert.All(model.MapDetails, r => Assert.Equal(editor.LastCaption, r.Caption));
            var retained = model.MapDetails;
            Assert.DoesNotContain(progress.GetVisualDescendants().OfType<ScrollViewer>(), s => s.IsEffectivelyVisible && s.Extent.Width > s.Viewport.Width + 1);
            Save(progress, $"shop-progress-map-details-{width}-{dark}");
            Click(progress.FindControl<Button>("ShopMapDetailsToggle")!); Assert.Equal(380, progress.Height);
            Click(progress.FindControl<Button>("ShopMapDetailsToggle")!);
            Assert.Same(retained, model.MapDetails); Assert.Equal(editorCalls, editor.Calls); Assert.Null(model.Error);
            var final = model.Status; await Task.Delay(250, Token); Pump(); Assert.Equal(final, model.Status);
            Assert.Equal(WorkflowShopStage.Clipboard, model.Progress!.Stage); Assert.True(f.Model.Orders.Single().HasShopHandoff);
            Assert.True(progress.IsVisible);
            await WorkflowTestWindows.AcknowledgeShopProgressAsync(window); Assert.Empty(window.OwnedWindows);
            Assert.Empty(log.Errors);
        }
        finally { editor.Release.TrySetResult(); await Until(() => !f.Model.IsBusy); foreach (var child in window.OwnedWindows.OfType<WorkflowShopProgressWindow>().ToArray()) child.Finish(true); WorkflowTestWindows.Close(window); }
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailureRemainsVisibleAndCancellationWaitsForASafeStop(bool fail)
    {
        var editor = new GateEditor { Fail = fail };
        using var f = new ProjectFixture(wordMapEditor: editor); await WorkflowShopAutomationWindowTests.ReadyHans(f);
        var window = new RecordingWindow(new(Path.Combine(f.DirectoryPath, "preferences.json"))) { DataContext = f.Model, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy); Click(window.FindControl<Button>("CopyWorkflowCuttingTask")!);
            await editor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await Until(() => window.OwnedWindows.OfType<WorkflowShopProgressWindow>().Any());
            var progress = Assert.Single(window.OwnedWindows.OfType<WorkflowShopProgressWindow>());
            var model = Assert.IsType<WorkflowShopTransferViewModel>(progress.DataContext);
            if (!fail)
            {
                progress.Close(); Assert.True(progress.IsVisible); Assert.True(model.CancellationRequested); Assert.False(model.CanCancel);
                Assert.True(f.Model.IsBusy); Assert.Contains("Останавливаем", model.ProgressTitle);
                Save(progress, "shop-progress-stopping");
            }
            editor.Release.SetResult(); await Until(() => !f.Model.IsBusy);
            Assert.Null(window.Copied); Assert.False(Directory.Exists(f.Model.ShopRoot)); Assert.False(f.Model.Orders.Single().HasShopHandoff);
            if (fail)
            {
                await Until(() => progress.FindControl<Button>("ShopProgressClose")!.IsEffectivelyVisible);
                Assert.True(progress.IsVisible); Assert.True(model.HasError); Assert.Contains("Word", model.Error);
                Assert.True(model.HasMapDetails); Assert.Empty(model.MapDetails);
                Click(progress.FindControl<Button>("ShopMapDetailsToggle")!);
                Assert.All(model.MapDetails, r => { Assert.True(r.NeedsAttention); Assert.Null(r.Caption); });
                Assert.Contains(progress.GetVisualDescendants().OfType<SelectableTextBlock>(), text => text.IsEffectivelyVisible && text.Text == model.Error);
                Save(progress, "shop-progress-error");
                Click(progress.FindControl<Button>("ShopProgressClose")!); await Until(() => !progress.IsVisible);
            }
            else
            {
                await Until(() => progress.FindControl<Button>("ShopProgressClose")!.IsVisible);
                Assert.True(progress.IsVisible); Assert.True(model.WasCancelled); Assert.Equal("Подготовка остановлена", model.ProgressTitle);
                Assert.False(model.HasMapDetails); Assert.Empty(model.MapDetails);
                await WorkflowTestWindows.AcknowledgeShopProgressAsync(window);
            }
        }
        finally { editor.Release.TrySetResult(); await Until(() => !f.Model.IsBusy); foreach (var child in window.OwnedWindows.OfType<WorkflowShopProgressWindow>().ToArray()) child.Finish(true); WorkflowTestWindows.Close(window); }
    }

    [AvaloniaFact]
    public async Task LargeResultIsLazyVirtualizedAndCanBeReadWithoutAnyIo()
    {
        using var f = new ShopFixture(); var requests = 0;
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "test", f.Order, f.Book,
            _ => { requests++; throw new InvalidOperationException("No database reads expected"); },
            _ => { requests++; throw new InvalidOperationException("No clipboard writes expected"); })
        { AllPrepared = true, TextCopied = true };
        var results = Enumerable.Range(0, 2000).Select(i => new MapSigningResult(f.Project.Id, f.Project.Name,
            Path.Combine(f.Source, $"Карта-{i}.pdf"), MapSigningStatus.Signed, 2, "Подписано страниц: 2 из 2") { Caption = "БЛЦБ-009337 · ЗПТО" }).ToArray();
        model.RememberMapSigningResults(results);
        Directory.Delete(f.Source, true);
        var window = new WorkflowShopProgressWindow { DataContext = model };
        try
        {
            window.Show(); window.Finish(); Pump();
            Assert.Empty(model.MapDetails); Assert.Equal("Карты: обновлено файлов 2000, без изменений 0.", model.MapSigningSummary);
            var list = window.FindControl<ListBox>("ShopMapDetails")!;
            Assert.Empty(list.GetVisualDescendants().OfType<ListBoxItem>());
            Click(window.FindControl<Button>("ShopMapDetailsToggle")!);
            await Until(() => list.GetVisualDescendants().OfType<ListBoxItem>().Any());
            Assert.Same(results, model.MapDetails);
            Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 40);
            list.ScrollIntoView(results[^1]); Pump();
            await Until(() => list.GetVisualDescendants().OfType<ListBoxItem>().Any(i => ReferenceEquals(i.DataContext, results[^1])));
            Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 40);
            Click(window.FindControl<Button>("ShopMapDetailsToggle")!);
            Click(window.FindControl<Button>("ShopMapDetailsToggle")!);
            Assert.Same(results, model.MapDetails); Assert.Equal(0, requests);
            model.RememberMapSigningResults([results[0] with { Status = MapSigningStatus.Unchanged }]);
            Assert.Equal("Карты: обновлено файлов 0, без изменений 1.", model.MapSigningSummary);
            await model.RefreshAsync(); Assert.Equal(1, requests);
            Assert.False(model.HasMapDetails); Assert.False(model.MapDetailsExpanded); Assert.Empty(model.MapDetails); Assert.Empty(model.MapSigningSummary);
        }
        finally { window.Finish(true); }
    }

    private sealed class GateEditor : IWorkflowWordMapEditor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }
        public int Calls { get; private set; }
        public string? LastCaption { get; private set; }
        public async Task<WordMapEdit?> PrepareAsync(byte[] source, string projectFileName, string caption, CancellationToken token)
        {
            Calls++; LastCaption = caption;
            Entered.TrySetResult(); await Release.Task; // Ignore cancellation until released: the UI must wait, never kill user processes.
            token.ThrowIfCancellationRequested();
            if (Fail) throw new WordMapUnavailableException("Microsoft Word недоступен. Карта не изменена.");
            return await new ShopWordEditor().PrepareAsync(source, projectFileName, caption, token);
        }
    }
    private sealed class ImmediateEditor(Func<WordMapEdit?> prepare) : IWorkflowWordMapEditor
    {
        public Task<WordMapEdit?> PrepareAsync(byte[] source, string projectFileName, string caption, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(prepare()); }
    }
    private sealed class RecordingWindow(WorkflowPreferencesStore preferences) : WorkflowWindow(preferences)
    {
        public string? Copied { get; private set; }
        protected override Task WriteCuttingTaskClipboardAsync(string text) { Copied = text; return Task.CompletedTask; }
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 800 && !done(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(done()); }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!; var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(p, MouseButton.Left); root.MouseUp(p, MouseButton.Left); Pump();
    }
    private static void Save(TopLevel window, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } folder) return;
        Directory.CreateDirectory(folder); Pump(); using var frame = window.CaptureRenderedFrame(); Assert.NotNull(frame);
        frame.Save(Path.Combine(folder, name + ".png"), new PngBitmapEncoderOptions());
    }
}
