using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowPersistentHandoffTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void MarkerRetainsTheTransferAndIgnoresPriceCoefficientCommentsAndDecimalScale()
    {
        using var f = new ShopFixture("HansLaser"); var order = f.Order; var actor = f.Book.Employees.First(e => e.CanWork).Id;
        var initial = order.Specifications.Single().Current; var p = initial.Parameters;
        var rate = new CuttingRate(initial.Calculation.RateId, p.Machine, p.MaterialGroup, p.ThicknessMm,
            initial.Calculation.PricePerMeter, initial.Calculation.PricePerPierce) { MachineId = p.MachineId };
        var marked = WorkflowChanges.RecordShopHandoff(f.Book with { Rates = [rate] }, order.Id, order.Version, actor, DateTimeOffset.UtcNow);
        var before = marked.Orders.Single(); var spec = before.Specifications.Single();
        var changed = WorkflowChanges.SaveSpecification(marked, order.Id, before.Version, spec.Id,
            spec.Current.Parameters with { ManualCoefficient = 1.3m, Comment = "Internal" }, spec.Current.DocumentNumber, spec.Current.DocumentDate,
            actor, DateTimeOffset.UtcNow, spec.Current.SourcePath, spec.Current.ImportProfile, report: spec.Current.Report).Orders.Single();
        Assert.True(changed.HasShopHandoff); Assert.True(changed.HasCurrentShopHandoff); Assert.Equal(before.ShopHandoff, changed.ShopHandoff);
        var scaled = changed with { Projects = changed.Projects.Select(p => p with { ThicknessMm = decimal.Parse(p.ThicknessMm.ToString("0.0000000000", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture) }).ToArray() };
        Assert.Equal(WorkflowShopHandoff.Hash(changed), WorkflowShopHandoff.Hash(scaled));
        var blcb = changed with { Version = changed.Version + 1, Blcb = "9999" };
        Assert.True(blcb.HasShopHandoff); Assert.False(blcb.HasCurrentShopHandoff); Assert.Equal(before.ShopHandoff, blcb.ShopHandoff);
    }

    [Fact]
    public void IncludedProjectChangesMatterButExcludedDraftChangesDoNot()
    {
        using var f = new ShopFixture("HansLaser"); var actor = f.Book.Employees.First(e => e.CanWork).Id;
        var marked = WorkflowChanges.RecordShopHandoff(f.Book, f.Order.Id, f.Order.Version, actor, DateTimeOffset.UtcNow); var order = marked.Orders.Single();
        var project = order.Projects.Single(); var other = project with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Other" };
        var excluded = order with { Version = order.Version + 1, Projects = [.. order.Projects, other], Items = [.. order.Items, new(other.ItemId, 2, other.Id) { IsIncluded = false }] };
        Assert.True(excluded.HasCurrentShopHandoff);
        var included = excluded with { Items = excluded.Items.Select(i => i.Id == other.ItemId ? i with { IsIncluded = true } : i).ToArray() };
        Assert.True(included.HasShopHandoff); Assert.False(included.HasCurrentShopHandoff);
        var variant = order with { Projects = [project, other with { ItemId = project.ItemId, Variant = 2 }], Items = order.Items.Select(i => i with { CurrentProjectId = other.Id }).ToArray() };
        Assert.False(variant.HasCurrentShopHandoff);
    }

    [Fact]
    public async Task MdbClearIsSharedMetadataAndDoesNotEraseANewerTransferOrReplayAnUnknownWrite()
    {
        using var f = new ShopFixture("HansLaser"); var wire = new MemoryMdb { Book = f.Book }; var actor = f.Book.Employees.First(e => e.CanWork).Id;
        var a = new WorkflowMdbStore(Path.Combine(f.Root, "test.mdb"), wire, wire.Id); var b = new WorkflowMdbStore(a.FilePath, wire, wire.Id);
        await a.InspectAsync(Token); await b.InspectAsync(Token);
        var book = await a.RecordShopHandoffAsync(f.Order.Id, f.Order.Version, actor, DateTimeOffset.UtcNow, Token); var old = book.Orders.Single();
        var newer = await b.RecordShopHandoffAsync(old.Id, old.Version, actor, old.ShopHandoff!.At.AddMinutes(1), Token);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => a.ClearShopHandoffAsync(old.Id, old.Version, WorkflowHandoffClearExpected.From(old), actor, Token));
        var current = newer.Orders.Single(); var cleared = await a.ClearShopHandoffAsync(current.Id, current.Version, WorkflowHandoffClearExpected.From(current), actor, Token);
        Assert.Null(cleared.Orders.Single().ShopHandoff); Assert.Equal(current.Version, cleared.Orders.Single().Version);
        Assert.Equal(JsonSerializer.Serialize(current.Specifications), JsonSerializer.Serialize(cleared.Orders.Single().Specifications));
        Assert.Null((await b.ReadAsync(Token)).Orders.Single().ShopHandoff);
        current = (await a.RecordShopHandoffAsync(current.Id, current.Version, actor, current.ShopHandoff!.At.AddMinutes(2), Token)).Orders.Single();
        wire.LoseNextWriteReply = true;
        await Assert.ThrowsAsync<WorkflowOutcomeUnknownException>(() => a.ClearShopHandoffAsync(current.Id, current.Version, WorkflowHandoffClearExpected.From(current), actor, Token));
        var requests = wire.Requests.Count;
        await Assert.ThrowsAsync<WorkflowValidationException>(() => a.ClearShopHandoffAsync(current.Id, current.Version, WorkflowHandoffClearExpected.From(current), actor, Token));
        Assert.Equal(requests, wire.Requests.Count); Assert.Null((await a.ReadAsync(Token)).Orders.Single().ShopHandoff);
    }

    [Fact]
    public async Task OldJsonMarksKeepTheirDateAndUseAConservativeFallbackWithAnExactBackup()
    {
        using var f = new ShopFixture("HansLaser"); var order = f.Order; var actor = f.Book.Employees.First(e => e.CanWork).Id;
        var old = f.Book with { SchemaVersion = 12, Orders = [order with { ShopHandoff = new(order.Version, actor, DateTimeOffset.UtcNow) }] };
        var path = Path.Combine(f.Root, "old.json"); var bytes = JsonSerializer.SerializeToUtf8Bytes(old); await File.WriteAllBytesAsync(path, bytes, Token);
        var current = await new LocalWorkflowWorkspaceStore(path).ReadAsync(Token); Assert.Equal(WorkflowWorkspace.CurrentSchema, current.SchemaVersion);
        Assert.True(current.Orders.Single().HasCurrentShopHandoff); Assert.Equal(old.Orders.Single().ShopHandoff, current.Orders.Single().ShopHandoff);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(f.Root, "*.bak")), Token));
        var changed = current.Orders.Single() with { Version = order.Version + 1 }; Assert.True(changed.HasShopHandoff); Assert.False(changed.HasCurrentShopHandoff);
        Assert.Contains(WorkflowMdbSchema.UpgradeSql(1), sql => sql.Contains("content_hash", StringComparison.Ordinal));
        Assert.Contains("ALTER TABLE [order_shop_handoffs] ADD COLUMN [content_hash] TEXT(64)", WorkflowMdbSchema.UpgradeSql(2));
        Assert.Contains(WorkflowMdbSchema.UpgradeSql(3), sql => sql.StartsWith("CREATE TABLE [project_shop_handoffs]", StringComparison.Ordinal));
        Assert.Contains(WorkflowMdbSchema.UpgradeSql(4), sql => sql.StartsWith("CREATE TABLE [project_shop_handoff_inputs]", StringComparison.Ordinal));
        Assert.Empty(WorkflowMdbSchema.UpgradeSql(5));
        Assert.Throws<WorkflowValidationException>(() => WorkflowMdbSchema.UpgradeSql(0).ToArray());
    }

    [AvaloniaTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task MarkerGetsAGapAndManualClearHasCancelPreservesDraftsResultsAndEveryFile(bool dark)
    {
        using var f = new ProjectFixture(); await WorkflowShopAutomationWindowTests.ReadyHans(f); using var log = new WorkflowWindowTests.BindingLog();
        var window = new WorkflowWindow(new(Path.Combine(f.DirectoryPath, "preferences.json"))) { DataContext = f.Model, Width = 1180, Height = 768, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            var transfer = await f.Model.PrepareShopTransferAsync(_ => Task.CompletedTask); Assert.True(transfer!.AllPrepared, transfer.Error); Pump();
            Border Marker() => window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WorkflowShopHandoffMarker");
            Border Middle() => Marker().GetVisualDescendants().OfType<Border>().Single(b => b.Name == "WorkflowShopHandoffMiddle");
            Assert.True(Marker().IsEffectivelyVisible); Assert.True(Middle().IsEffectivelyVisible);
            f.Model.SelectedDraft!.Coefficient = "1,3"; await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Pump();
            Assert.True(Middle().IsEffectivelyVisible); Assert.False(f.Model.Orders.Single().HasShopHandoffChanges);
            f.Model.Blcb = "9999"; await f.Model.SaveHeaderCommand.ExecuteAsync(null); Pump();
            Assert.True(Marker().IsEffectivelyVisible); Assert.False(Middle().IsEffectivelyVisible); Assert.Contains("После передачи были изменения", f.Model.Orders.Single().ShopHandoffHint);
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is { } markerOutput)
            { Directory.CreateDirectory(markerOutput); using var markerFrame = window.CaptureRenderedFrame(); markerFrame?.Save(Path.Combine(markerOutput, $"marker-changed-{dark}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            var oldStamp = f.Model.Orders.Single().Order.ShopHandoff;
            f.Model.GenerateResultCommand.Execute(null); var result = f.Model.ResultText;
            var files = Directory.GetFiles(transfer.Root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var beforeCancel = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
            var actions = window.FindControl<Button>("WorkflowOrderActions")!; actions.Flyout!.ShowAt(actions); Pump();
            window.FindControl<MenuItem>("ClearWorkflowShopHandoff")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => window.OwnedWindows.OfType<WorkflowHandoffClearWindow>().Any());
            var dialog = window.OwnedWindows.OfType<WorkflowHandoffClearWindow>().Single();
            dialog.FindControl<Button>("CancelHandoffClear")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => window.OwnedWindows.Count == 0);
            Assert.Equal(beforeCancel, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
            f.Model.SelectedDraft!.Coefficient = "1,5"; var draft = f.Model.SelectedDraft;
            actions.Flyout.ShowAt(actions); Pump(); window.FindControl<MenuItem>("ClearWorkflowShopHandoff")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Until(() => window.OwnedWindows.OfType<WorkflowHandoffClearWindow>().Any()); dialog = window.OwnedWindows.OfType<WorkflowHandoffClearWindow>().Single();
            if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is { } output)
            { Directory.CreateDirectory(output); using var frame = dialog.CaptureRenderedFrame(); frame?.Save(Path.Combine(output, $"marker-clear-dialog-{dark}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
            dialog.FindControl<Button>("ConfirmHandoffClear")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && !f.Model.IsBusy && !f.Model.Orders.Single().HasShopHandoff); Pump();
            Assert.Null(f.Model.Orders.Single().Order.ShopHandoff); Assert.Null(f.Model.ShopTransfer); Assert.False(Marker().IsEffectivelyVisible); Assert.Same(draft, f.Model.SelectedDraft); Assert.True(draft.IsDirty);
            Assert.Equal(result, f.Model.ResultText); Assert.False(f.Model.ResultIsStale);
            foreach (var file in files) Assert.Equal(file.Value, await File.ReadAllBytesAsync(file.Key, Token));
            Assert.NotNull(oldStamp); Assert.Empty(log.Errors);
        }
        finally { WorkflowTestWindows.Close(window); }
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> done)
    { for (var i = 0; i < 500 && !done(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(done()); }
}
