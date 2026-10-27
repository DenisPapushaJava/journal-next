using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jornal.Desktop.Controls;
using Jornal.Desktop.ViewModels;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowShopHandoffTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void HandoffMetadataRoundtripsWithoutChangingTheCalculationOrItsVersion()
    {
        using var f = new ShopFixture("HansLaser"); var order = f.Order; var actor = f.Book.Employees.First(e => e.CanWork).Id;
        var book = WorkflowChanges.RecordShopHandoff(f.Book, order.Id, order.Version, actor, DateTimeOffset.UtcNow);
        book.Validate(); var marked = book.Orders.Single(); Assert.True(marked.HasCurrentShopHandoff);
        Assert.Equal(order.Version, marked.Version); Assert.Equal(order.UpdatedAt, marked.UpdatedAt);
        Assert.Equal(JsonSerializer.Serialize(order.Specifications), JsonSerializer.Serialize(marked.Specifications));
        var rows = WorkflowMdbMapping.Flatten(book);
        Assert.Single(rows["order_shop_handoffs"]); Assert.Equal(actor, rows["order_shop_handoffs"][order.Id].Values["actor_id"]);
        var roundtrip = WorkflowMdbMapping.Orders(rows).Single(); Assert.Equal(marked.ShopHandoff, roundtrip.ShopHandoff);
        Assert.Same(book, WorkflowChanges.RecordShopHandoff(book, order.Id, order.Version, actor, marked.ShopHandoff!.At));
        var changed = WorkflowChanges.UpdateOrder(book, order.Id, order.Version, order.ManagerId, order.TechnologistId, "new", actor, DateTimeOffset.UtcNow);
        Assert.False(changed.Orders.Single().HasCurrentShopHandoff);
        Assert.Throws<WorkflowConflictException>(() => WorkflowChanges.RecordShopHandoff(changed, order.Id, order.Version, actor, DateTimeOffset.UtcNow));
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.RecordShopHandoff(book, order.Id, order.Version, Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task MdbMarkIsSharedAndUnknownOutcomeCannotBeReplayedWithoutRefresh()
    {
        using var f = new ShopFixture("HansLaser"); var wire = new MemoryMdb { Book = f.Book };
        var path = Path.Combine(f.Root, "test.mdb"); var a = new WorkflowMdbStore(path, wire, wire.Id); var b = new WorkflowMdbStore(path, wire, wire.Id);
        var actor = f.Book.Employees.First(e => e.CanWork).Id;
        await a.InspectAsync(Token); var before = await b.ReadAsync(Token);
        var marked = await a.RecordShopHandoffAsync(f.Order.Id, f.Order.Version, actor, DateTimeOffset.UtcNow, Token);
        Assert.True(marked.Orders.Single().HasCurrentShopHandoff); Assert.Equal(before.Orders.Single().Version, marked.Orders.Single().Version);
        Assert.True((await b.ReadAsync(Token)).Orders.Single().HasCurrentShopHandoff);
        wire.LoseNextWriteReply = true;
        await Assert.ThrowsAsync<WorkflowOutcomeUnknownException>(() => a.RecordShopHandoffAsync(f.Order.Id, f.Order.Version, actor, DateTimeOffset.UtcNow.AddMinutes(1), Token));
        var requests = wire.Requests.Count;
        await Assert.ThrowsAsync<WorkflowValidationException>(() => a.RecordShopHandoffAsync(f.Order.Id, f.Order.Version, actor, DateTimeOffset.UtcNow, Token));
        Assert.Equal(requests, wire.Requests.Count); await a.ReadAsync(Token); Assert.False(a.RequiresRefresh);
        Assert.True((await b.ReadAsync(Token)).Orders.Single().HasCurrentShopHandoff);
    }

    [Fact]
    public async Task VersionElevenJsonIsBackedUpAndNewMetadataSurvivesRestart()
    {
        using var f = new ShopFixture("HansLaser"); var path = Path.Combine(f.Root, "workspace.json");
        var old = JsonSerializer.SerializeToNode(f.Book)!.AsObject(); old["SchemaVersion"] = 11;
        await File.WriteAllTextAsync(path, old.ToJsonString(), Token); var bytes = await File.ReadAllBytesAsync(path, Token);
        IWorkflowWorkspaceStore store = new LocalWorkflowWorkspaceStore(path);
        var book = await store.ReadAsync(Token); Assert.Equal(WorkflowWorkspace.CurrentSchema, book.SchemaVersion);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(f.Root, "*.bak")), Token));
        var actor = book.Employees.First(e => e.CanWork).Id;
        await store.RecordShopHandoffAsync(f.Order.Id, f.Order.Version, actor, DateTimeOffset.UtcNow, Token);
        var restarted = await new LocalWorkflowWorkspaceStore(path).ReadAsync(Token);
        Assert.True(restarted.Orders.Single().HasCurrentShopHandoff);
    }

    [AvaloniaTheory]
    [InlineData(false, 1180)] [InlineData(true, 1180)] [InlineData(false, 1366)] [InlineData(true, 1366)]
    public async Task MarkerHasOnlyTooltipAndColorPickerIsLocalAndIndependentOfAuthor(bool dark, int width)
    {
        using var log = new WorkflowWindowTests.BindingLog(); using var f = new ProjectFixture(); await WorkflowShopAutomationWindowTests.ReadyHans(f);
        var actor = f.Model.CurrentEmployee!;
        await f.Store.SaveEmployeeProfileAsync(actor, actor.Name, 0xFF80FF80, Token);
        var preferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "preferences.json")); preferences.Update(p => p with { DarkTheme = dark });
        var window = new WorkflowWindow(preferences) { DataContext = f.Model, Width = width, Height = 768 };
        WorkflowViewModel? colleague = null;
        try
        {
            window.Show(); await Until(() => !f.Model.IsBusy);
            f.Model.GenerateResultCommand.Execute(null); var resultText = f.Model.ResultText; Assert.False(f.Model.ResultIsStale);
            var failClipboard = true;
            var preview = await f.Model.PrepareShopTransferAsync(_ => failClipboard ? throw new IOException("clipboard") : Task.CompletedTask);
            Assert.NotNull(preview); Assert.True(preview.AllPrepared); Assert.False(preview.TextCopied);
            var row = Assert.Single(f.Model.Orders); Assert.True(row.HasShopHandoff); Assert.Contains(actor.Name, row.ShopHandoffHint);
            Assert.Equal(resultText, f.Model.ResultText); Assert.False(f.Model.ResultIsStale);
            failClipboard = false; await f.Model.RetryShopTransferTextAsync(); Assert.True(preview.TextCopied);
            Pump(); var marker = Assert.Single(window.GetVisualDescendants().OfType<Border>(), b => b.Name == "WorkflowShopHandoffMarker");
            Assert.True(marker.IsEffectivelyVisible); Assert.Equal(4, marker.Bounds.Width); Assert.Contains("Передано в цех", row.NumberHint);
            var author = Assert.Single(window.GetVisualDescendants().OfType<Border>(), b => b.Name == "WorkflowCreatorMarker");
            Assert.Equal(0xFF80FF80u, BrushColor(author.Background)); Frame(window, $"marker-{width}-{dark}");
            var savedBytes = await File.ReadAllBytesAsync(f.Store.FilePath, Token);
            Click(window.FindControl<Button>("WorkflowSettings")!);
            var settings = Assert.IsType<WorkflowSettingsWindow>(Assert.Single(window.OwnedWindows));
            var vm = Assert.IsType<WorkflowSettingsViewModel>(settings.DataContext); var originalProfile = vm.ProfileColorArgb;
            var shopButton = settings.FindControl<Button>("ChangeWorkflowShopColor")!;
            var profileButton = settings.FindControl<Button>("ChangeWorkflowProfileColor")!;
            Click(shopButton); Assert.True(profileButton.Flyout!.IsOpen); Assert.True(vm.IsEditingShopColor);
            var palette = settings.FindControl<StackPanel>("WorkflowProfilePalette")!;
            var color = vm.OtherColors.First(c => c.Name == "Оранжевый");
            Frame(TopLevel.GetTopLevel(palette)!, $"marker-palette-{width}-{dark}");
            Click(palette.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.DataContext, color)));
            Assert.False(profileButton.Flyout.IsOpen); Assert.True(shopButton.IsFocused);
            Assert.Equal(color.Argb, preferences.Load().ShopHandoffColorArgb); Assert.Equal(color.Argb, BrushColor(marker.Background));
            Assert.Equal(originalProfile, vm.ProfileColorArgb); Assert.False(vm.HasChanges);
            Click(shopButton); Click(settings.FindControl<Button>("MoreWorkflowProfileColors")!);
            var wheel = settings.FindControl<ColorWheel>("WorkflowProfileColor")!;
            wheel.HsvColor = new(Color.Parse("#456789")); Pump();
            var precise = vm.PickerSpectrumColor.ToRgb().ToUInt32(); Assert.Equal(color.Argb, preferences.Load().ShopHandoffColorArgb);
            Frame(TopLevel.GetTopLevel(wheel)!, $"marker-precise-{width}-{dark}");
            Click(settings.FindControl<Button>("ConfirmWorkflowProfileColor")!);
            Assert.Equal(precise, preferences.Load().ShopHandoffColorArgb); Assert.Equal(precise, BrushColor(marker.Background));
            Assert.Equal(0xFF80FF80u, BrushColor(author.Background)); Assert.Equal(originalProfile, vm.ProfileColorArgb);
            Assert.Equal(savedBytes, await File.ReadAllBytesAsync(f.Store.FilePath, Token));
            Click(shopButton); Click(settings.FindControl<Button>("MoreWorkflowProfileColors")!);
            wheel.HsvColor = new(Color.Parse("#FEDCBA")); Pump();
            TopLevel.GetTopLevel(wheel)!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Assert.Equal(precise, preferences.Load().ShopHandoffColorArgb); Assert.True(shopButton.IsFocused);
            Frame(settings, $"marker-settings-{width}-{dark}"); Click(settings.FindControl<Button>("CloseWorkflowSettings")!);
            var otherPreferences = new WorkflowPreferencesStore(Path.Combine(f.DirectoryPath, "colleague.json"));
            otherPreferences.Save(new(dark) { EmployeeId = actor.Id, ShopHandoffColorArgb = 0xFFFF9DFF });
            colleague = new(new LocalWorkflowWorkspaceStore(f.Store.FilePath), new AppUpdatesViewModel(new WorkflowAppUpdater()), preferences: otherPreferences);
            await colleague.ReloadCommand.ExecuteAsync(null);
            Assert.True(colleague.Orders.Single().HasShopHandoff); Assert.Equal(0xFFFF9DFFu, BrushColor(colleague.Orders.Single().ShopHandoffBrush));
            Assert.Equal(precise, f.Model.ShopHandoffColorArgb);
            f.Model.Blcb = "0100"; await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
            Assert.False(f.Model.Orders.Single().HasShopHandoff);
            await colleague.ReloadCommand.ExecuteAsync(null); Assert.False(colleague.Orders.Single().HasShopHandoff);
            Assert.Empty(log.Errors);
        }
        finally { foreach (var child in window.OwnedWindows.ToArray()) child.Close(); window.Close(); colleague?.Detach(); }
    }

    [Fact]
    public async Task PartialCopyDoesNotRecordHandoff()
    {
        using var f = new ShopFixture("HansLaser");
        var source = Path.Combine(f.Root, "second"); Directory.CreateDirectory(source);
        var second = f.Project with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Другой проект", NativePath = Path.Combine(source, "другой.rcam") };
        await File.WriteAllTextAsync(second.NativePath!, "native", Token);
        await File.WriteAllTextAsync(Path.Combine(source, "лист.GNC"), "program", Token);
        await File.WriteAllTextAsync(Path.Combine(source, "карта.DOC"), "map", Token);
        var order = f.Order with { Projects = [f.Project, second], Items = [.. f.Order.Items, new(second.ItemId, 2, second.Id)],
            Specifications = [.. f.Order.Specifications, f.Order.Specifications.Single() with { Id = second.Id }] };
        var book = f.Book with { Orders = [order] }; var validations = 0; var recorded = 0;
        var vm = new WorkflowShopTransferViewModel(f.ShopRoot, "test-db", order, book,
            _ => { if (++validations == 3) throw new WorkflowValidationException("Заявка изменилась перед вторым проектом."); return Task.CompletedTask; }, _ => Task.CompletedTask,
            recordHandoff: (_, _) => { recorded++; return Task.CompletedTask; });
        await vm.RefreshAsync(); validations = 0; Assert.True(vm.CanStartAutomatically);
        await vm.CopyAsync(); Assert.False(vm.AllPrepared); Assert.False(vm.TextCopied); Assert.Equal(0, recorded);
        Assert.Single(vm.Projects, p => p.HasDestination);
    }

    private static uint BrushColor(IBrush? brush) => Assert.IsType<SolidColorBrush>(brush).Color.ToUInt32();
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static async Task Until(Func<bool> ready)
    { for (var i = 0; i < 500 && !ready(); i++) { await Task.Delay(10, Token); Pump(); } Pump(); Assert.True(ready()); }
    private static void Click(Control control)
    {
        var root = TopLevel.GetTopLevel(control)!; var p = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)!.Value;
        root.MouseDown(p, MouseButton.Left); root.MouseUp(p, MouseButton.Left); Pump();
    }
    private static void Frame(TopLevel root, string name)
    {
        if (Environment.GetEnvironmentVariable("JOURNAL_WORKFLOW_SCREENSHOTS") is not { } output) return;
        Directory.CreateDirectory(output); root.CaptureRenderedFrame()!.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}
