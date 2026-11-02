using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowProjectHandoffTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<(CuttingProject First, CuttingProject Second)> Ready(ProjectFixture f)
    {
        await f.InitializeAsync(); var a = await f.AddAsync(); var b = await f.AddAsync();
        await ProjectFixture.WriteAsync(a); await ProjectFixture.WriteAsync(b); await f.AcceptAsync();
        f.Model.Blcb = "9337"; await f.Model.SaveHeaderCommand.ExecuteAsync(null); return (a, b);
    }
    private static WorkflowWorkspace Mark(WorkflowWorkspace book, Guid[]? ids = null, DateTimeOffset? at = null)
    {
        var o = book.Orders.Single(); var actor = o.TechnologistId;
        return ids is null ? WorkflowChanges.RecordShopHandoff(book, o.Id, o.Version, actor, at ?? DateTimeOffset.UtcNow)
            : WorkflowChanges.RecordProjectHandoffs(book, o.Id, o.Version, ids, actor, at ?? DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task MarksBelongToActuallyIncludedProjectsAndSurviveExclusionAndResendingOtherProjects()
    {
        using var f = new ProjectFixture(); var (a, b) = await Ready(f); await f.Model.SetProjectIncludedAsync(b.Id, false);
        var book = Mark(await f.Store.ReadAsync(Token)); var order = book.Orders.Single(); var stamp = order.Projects.Single(p => p.Id == a.Id).ShopHandoff;
        Assert.NotNull(stamp); Assert.Null(order.Projects.Single(p => p.Id == b.Id).ShopHandoff);
        book = WorkflowProjects.SetIncluded(book, order.Id, order.Version, a.ItemId, false, order.TechnologistId, DateTimeOffset.UtcNow); order = book.Orders.Single();
        Assert.Equal(stamp, order.Projects.Single(p => p.Id == a.Id).ShopHandoff); Assert.Equal(stamp!.ContentHash, WorkflowShopHandoff.ProjectHash(order, a.Id));
        book = WorkflowProjects.SetIncluded(book, order.Id, order.Version, b.ItemId, true, order.TechnologistId, DateTimeOffset.UtcNow);
        book = Mark(book, at: stamp.At.AddMinutes(1)); order = book.Orders.Single();
        Assert.Equal(stamp, order.Projects.Single(p => p.Id == a.Id).ShopHandoff); Assert.NotNull(order.Projects.Single(p => p.Id == b.Id).ShopHandoff);
        var next = a with { Id = Guid.NewGuid(), Variant = 2, ShopHandoff = stamp };
        Assert.Throws<WorkflowValidationException>(() => WorkflowProjects.Add(book, order.Id, order.Version, next, order.TechnologistId, DateTimeOffset.UtcNow));
        var clear = WorkflowChanges.ClearShopHandoff(book, order.Id, order.Version, WorkflowHandoffClearExpected.From(order), order.TechnologistId);
        Assert.Null(clear.Orders.Single().ShopHandoff); Assert.All(clear.Orders.Single().Projects, p => Assert.Null(p.ShopHandoff));
        Assert.Equal(order.Version, clear.Orders.Single().Version);
    }

    [Fact]
    public async Task ProjectFingerprintIgnoresPriceAndOtherProjectsButDetectsItsOwnNewReportAndHeaderChanges()
    {
        using var f = new ProjectFixture(); var (a, b) = await Ready(f); var book = Mark(await f.Store.ReadAsync(Token)); var order = book.Orders.Single();
        var aHash = order.Projects.Single(p => p.Id == a.Id).ShopHandoff!.ContentHash;
        var bHash = order.Projects.Single(p => p.Id == b.Id).ShopHandoff!.ContentHash;
        var spec = order.Specifications.Single(s => s.Id == b.Id).Current;
        book = WorkflowChanges.SaveSpecification(book, order.Id, order.Version, b.Id, spec.Parameters with { HasChamfer = true, ManualCoefficient = 1.5m },
            spec.DocumentNumber, spec.DocumentDate, order.TechnologistId, DateTimeOffset.UtcNow, spec.SourcePath, spec.ImportProfile, report: spec.Report); order = book.Orders.Single();
        Assert.Equal(aHash, WorkflowShopHandoff.ProjectHash(order, a.Id)); Assert.NotEqual(bHash, WorkflowShopHandoff.ProjectHash(order, b.Id));
        var priceOnly = order with { Specifications = order.Specifications.Select(s => s.Id == a.Id ? s with { Current = s.Current with { Parameters = s.Current.Parameters with { ManualCoefficient = 1.3m } } } : s).ToArray() };
        Assert.Equal(aHash, WorkflowShopHandoff.ProjectHash(priceOnly, a.Id));
        Assert.NotEqual(aHash, WorkflowShopHandoff.ProjectHash(order with { Blcb = "9999" }, a.Id));
    }

    [Fact]
    public async Task PartialTransferIsSharedAndConcurrentClearCannotEraseANewerProjectMark()
    {
        using var f = new ProjectFixture(); var (a, b) = await Ready(f); var wire = new MemoryMdb { Book = await f.Store.ReadAsync(Token) };
        var store = new WorkflowMdbStore(Path.Combine(f.DirectoryPath, "memory.mdb"), wire, wire.Id); await store.InspectAsync(Token);
        var order = wire.Book.Orders.Single(); var old = WorkflowHandoffClearExpected.From(order);
        var book = await store.RecordProjectHandoffsAsync(order.Id, order.Version, [a.Id], order.TechnologistId, DateTimeOffset.UtcNow, Token); order = book.Orders.Single();
        Assert.Null(order.ShopHandoff); Assert.NotNull(order.Projects.Single(p => p.Id == a.Id).ShopHandoff); Assert.Null(order.Projects.Single(p => p.Id == b.Id).ShopHandoff);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => store.ClearShopHandoffAsync(order.Id, order.Version, old, order.TechnologistId, Token));
        var mapped = WorkflowMdbMapping.Flatten(book); Assert.Single(mapped["project_shop_handoffs"]);
        var round = WorkflowMdbMapping.Orders(mapped).Single(); Assert.Equal(order.Projects.Single(p => p.Id == a.Id).ShopHandoff, round.Projects.Single(p => p.Id == a.Id).ShopHandoff);
        wire.LoseNextWriteReply = true;
        await Assert.ThrowsAsync<WorkflowOutcomeUnknownException>(() => store.RecordProjectHandoffsAsync(order.Id, order.Version, [b.Id], order.TechnologistId, DateTimeOffset.UtcNow, Token));
        var count = wire.Requests.Count;
        await Assert.ThrowsAsync<WorkflowValidationException>(() => store.RecordProjectHandoffsAsync(order.Id, order.Version, [b.Id], order.TechnologistId, DateTimeOffset.UtcNow, Token));
        Assert.Equal(count, wire.Requests.Count);
        Assert.All((await store.ReadOrderAsync(order.Id, Token)).Orders.Single().Projects, p => Assert.NotNull(p.ShopHandoff));
    }

    [Theory]
    [InlineData(12)] [InlineData(13)]
    public async Task ExistingJsonUpgradesWithExactBackupButNeverInventsProjectMarks(int schema)
    {
        using var f = new ProjectFixture(); await Ready(f); var marked = Mark(await f.Store.ReadAsync(Token));
        var data = JsonSerializer.SerializeToNode(marked)!.AsObject(); data["SchemaVersion"] = schema;
        foreach (var p in data["Orders"]![0]!["Projects"]!.AsArray()) p!.AsObject().Remove("ShopHandoff");
        var path = Path.Combine(f.DirectoryPath, "old.json"); var bytes = System.Text.Encoding.UTF8.GetBytes(data.ToJsonString()); await File.WriteAllBytesAsync(path, bytes, Token);
        var store = new LocalWorkflowWorkspaceStore(path);
        using (File.Open(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAnyAsync<IOException>(() => store.ReadAsync(Token));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token));
        var book = await store.ReadAsync(Token); Assert.Equal(WorkflowWorkspace.CurrentSchema, book.SchemaVersion);
        Assert.Equal(marked.Orders.Single().ShopHandoff, book.Orders.Single().ShopHandoff); Assert.All(book.Orders.Single().Projects, p => Assert.Null(p.ShopHandoff));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(f.DirectoryPath, "old.json.*.bak")), Token));
        await store.ReadAsync(Token); Assert.Single(Directory.GetFiles(f.DirectoryPath, "old.json.*.bak"));
        var empty = await new LocalWorkflowWorkspaceStore(Path.Combine(f.DirectoryPath, "new.json")).ReadAsync(Token); Assert.Equal(WorkflowWorkspace.CurrentSchema, empty.SchemaVersion); Assert.Empty(empty.Orders);
    }

    [Fact]
    public async Task WorkReplacementCannotEraseAConcurrentTransferOrRestoreAConcurrentClear()
    {
        using var f = new ProjectFixture(); var (a, _) = await Ready(f); var original = await f.Store.ReadAsync(Token); var order = original.Orders.Single();
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.ClearShopHandoff(original, order.Id, order.Version, WorkflowHandoffClearExpected.From(order), order.TechnologistId));
        var stale = order with { Blcb = "9999", Version = order.Version + 1 };
        var marked = Mark(original).Orders.Single();
        var saved = WorkflowShopHandoff.PreserveRecorded(marked, stale);
        Assert.Equal(marked.ShopHandoff, saved.ShopHandoff);
        Assert.Equal(marked.Projects.Single(p => p.Id == a.Id).ShopHandoff, saved.Projects.Single(p => p.Id == a.Id).ShopHandoff);
        Assert.Equal("9999", saved.Blcb); Assert.False(saved.HasCurrentShopHandoff);
        var cleared = WorkflowChanges.ClearShopHandoff(original with { Orders = [saved] }, saved.Id, saved.Version, WorkflowHandoffClearExpected.From(saved), order.TechnologistId).Orders.Single();
        var later = WorkflowShopHandoff.PreserveRecorded(cleared, saved with { Version = saved.Version + 1 });
        Assert.Null(later.ShopHandoff); Assert.All(later.Projects, p => Assert.Null(p.ShopHandoff));
        var newOrder = WorkflowShopHandoff.PreserveRecorded(null, marked);
        Assert.Null(newOrder.ShopHandoff); Assert.All(newOrder.Projects, p => Assert.Null(p.ShopHandoff));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void OldMdbUpgradeAddsTheNewTableAndKeysWithoutTouchingProjectOrCalculationRows(int from)
    {
        var sql = WorkflowMdbSchema.UpgradeSql(from).ToArray();
        Assert.Single(sql, s => s.StartsWith("CREATE TABLE [project_shop_handoffs]", StringComparison.Ordinal));
        Assert.Contains(sql, s => s.Contains("REFERENCES [cutting_projects]", StringComparison.Ordinal));
        Assert.Contains(sql, s => s.Contains("REFERENCES [employees]", StringComparison.Ordinal) && s.Contains("[project_shop_handoffs]", StringComparison.Ordinal));
        Assert.DoesNotContain(sql, s => s.StartsWith("UPDATE ", StringComparison.Ordinal) || s.StartsWith("DELETE ", StringComparison.Ordinal) || s.StartsWith("DROP ", StringComparison.Ordinal));
        Assert.Contains(WorkflowMdbSchema.CreateSql(), s => s.StartsWith("CREATE TABLE [project_shop_handoffs]", StringComparison.Ordinal));
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task ProjectRowsShowOnlyConfirmedMarksAndExcludedRowsKeepTheMarkWithoutLosingSelection(bool dark)
    {
        using var f = new ProjectFixture(); var (a, b) = await Ready(f); await f.Model.SetProjectIncludedAsync(b.Id, false);
        await f.Model.SaveShopRootAsync(null, Path.Combine(f.DirectoryPath, "shop"), Token);
        ShopSigningTestData.WriteBlockMap(a, Path.Combine(Path.GetDirectoryName(a.NativePath!)!, "Карта.pdf"));
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(a.NativePath!)!, "Лист.cnc"), "UP", Token);
        using var log = new WorkflowWindowTests.BindingLog(); var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json")))
        { DataContext = f.Model, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = 1180, Height = 768 };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var row = f.Model.Projects.Single(r => r.Project.Id == a.Id); f.Model.SelectedProject = row;
            var transfer = await f.Model.PrepareShopTransferAsync(_ => Task.CompletedTask); Assert.True(transfer!.TextCopied, transfer.Error); Pump();
            Assert.Same(row, f.Model.Projects.Single(r => r.Project.Id == a.Id)); Assert.True(row.HasShopHandoff); Assert.False(row.HasShopHandoffChanges);
            Assert.False(f.Model.Projects.Single(r => r.Project.Id == b.Id).HasShopHandoff);
            f.Model.SelectedDraft!.HasChamfer = true;
            await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error); Pump();
            row = f.Model.Projects.Single(r => r.Project.Id == a.Id); Assert.True(row.HasShopHandoffChanges);
            Assert.Contains("были изменения", row.ShopHandoffHint);
            Assert.Contains("Добавлена фаска.", row.ShopHandoffHint); Assert.DoesNotContain("Точные отличия", row.ShopHandoffHint);
            await f.Model.SetProjectIncludedAsync(a.Id, false); Pump(); row = f.Model.Projects.Single(r => r.Project.Id == a.Id);
            Assert.True(row.IsExcluded); Assert.True(row.HasShopHandoff); Assert.True(row.HasShopHandoffChanges); Assert.Contains("исключён", row.ShopHandoffHint);
            var marker = window.GetVisualDescendants().OfType<Border>().Single(e => e.Name == "WorkflowProjectHandoffMarker" && ReferenceEquals(e.DataContext, row));
            Assert.True(marker.IsEffectivelyVisible); Assert.Equal(1, marker.Opacity);
            Assert.False(marker.GetVisualDescendants().OfType<Border>().Single(e => e.Name == "WorkflowProjectHandoffMiddle").IsVisible);
            window.MouseMove(marker.TranslatePoint(new Point(2, 8), window)!.Value); ToolTip.SetIsOpen(marker, true); Pump();
            var tip = Assert.IsType<TextBlock>(ToolTip.GetTip(marker)); await Until(() => tip.Bounds.Width > 0);
            Assert.Equal(row.ShopHandoffHint, tip.Text); Assert.Equal(TextWrapping.Wrap, tip.TextWrapping); Assert.InRange(tip.Bounds.Width, 1, 420);
            Assert.Equal(0, tip.MaxLines); Assert.False(Jornal.Desktop.Controls.TableCellPresentation.GetEnabled(tip)); Assert.True(tip.TextLayout.TextLines.Count >= 5);
            await Task.Delay(250, Token); Pump(); // Capture the settled tooltip, after the theme's opening transition.
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is { } output)
            { Directory.CreateDirectory(output); using var frame = (TopLevel.GetTopLevel(tip) ?? window).CaptureRenderedFrame(); frame?.Save(Path.Combine(output, $"project-reasons-{dark}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            ToolTip.SetIsOpen(marker, false);
            Assert.Empty(log.Errors);
        }
        finally { WorkflowTestWindows.Close(window); }
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(done()); }
}
