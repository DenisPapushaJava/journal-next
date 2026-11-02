using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowSignedTransferTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task TrashedIncludedPositionDoesNotBlockBulkSigningOrSendingTheRemainingProject(bool send)
    {
        using var f = new MapSigningFixture(); f.Create();
        await File.WriteAllTextAsync(Path.Combine(f.Root, "Лист1.cnc"), "UP", Token);
        var active = f.Project;
        var removed = active with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Проект AWJC в корзине",
            NativePath = Path.Combine(f.Root, "trash", "missing.dig"), DeletedAt = DateTimeOffset.UtcNow, DeletedBy = f.Order.TechnologistId };
        var excluded = active with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Исключённый проект",
            NativePath = Path.Combine(f.Root, "excluded", "missing.rcam") };
        f.Book = f.Book with { Orders = [f.Order with
        {
            Projects = [active, removed, excluded],
            Items = [new(removed.ItemId, 3, removed.Id), new(active.ItemId, 4, active.Id), new(excluded.ItemId, 1, excluded.Id) { IsIncluded = false }]
        }] };
        Assert.True(WorkflowCuttingTask.Prepare(f.Order, f.Book.Employees, f.Book.Customers, f.Book.Machines).IsReady);
        var shop = f.Root + "-shop";
        try
        {
            if (send)
            {
                var copied = 0;
                var model = new WorkflowShopTransferViewModel(shop, "db", f.Order, f.Book, _ => Task.CompletedTask,
                    _ => { copied++; return Task.CompletedTask; });
                await model.RefreshAsync(); Assert.True(model.CanStartAutomatically, model.Error);
                Assert.Equal(active.Id, Assert.Single(model.Projects).Plan.Project.Id);
                await model.CopyAsync(); Assert.True(model.AllPrepared, model.Error); Assert.True(model.TextCopied); Assert.Equal(1, copied);
                model.MapDetailsExpanded = true;
                Assert.Equal(active.Id, Assert.Single(model.MapDetails).ProjectId);
            }
            else
            {
                var result = Assert.Single(await f.Sign());
                Assert.Equal(active.Id, result.ProjectId); Assert.Equal(MapSigningStatus.Signed, result.Status);
            }
            Assert.False(Directory.Exists(Path.Combine(f.Root, "trash")));
            Assert.False(Directory.Exists(Path.Combine(f.Root, "excluded")));
        }
        finally { if (Directory.Exists(shop)) Directory.Delete(shop, true); }
    }

    [Theory]
    [InlineData("Лазер")]
    [InlineData("Плазма")]
    [InlineData("Газо-кислородная")]
    [InlineData("ГАР")]
    [InlineData("HansLaser")]
    public async Task EverySupportedMachineSignsBeforePublishingAndRepeatIsNoOp(string machine)
    {
        using var f = new MapSigningFixture(machine);
        var hans = machine == "HansLaser";
        var up = Path.Combine(f.Root, hans ? "Лист.GNC" : machine == "Лазер" ? "Лист1.cnc" : "Лист1.NC");
        await File.WriteAllTextAsync(up, "UP", Token);
        var map = hans ? Path.Combine(f.Root, "Карта.DOC") : machine == "ГАР" ? WaterjetMapFixture.Create(f) : f.Create();
        if (hans) await File.WriteAllTextAsync(map, "DOC fixture", Token);
        var original = await File.ReadAllBytesAsync(map, Token);
        // The destination must not be inside the working folder.
        var shop = f.Root + "-shop"; var marks = 0; var copies = 0;
        try
        {
            var model = new WorkflowShopTransferViewModel(shop, "db", f.Order, f.Book, _ => Task.CompletedTask,
                _ => { Assert.Equal(copies + 1, marks); copies++; return Task.CompletedTask; },
                recordHandoff: (_, _) => { marks++; return Task.CompletedTask; }, wordMapEditor: new ShopWordEditor());
            await model.RefreshAsync(); Assert.Null(model.Error);
            Assert.True(model.CanStartAutomatically);
            await model.CopyAsync(); Assert.True(model.AllPrepared, model.Error); Assert.True(model.TextCopied, model.Error);
            Assert.True(model.HasMapDetails); Assert.Empty(model.MapDetails);
            Assert.Equal("Карты: обновлено файлов 1, без изменений 0.", model.MapSigningSummary);
            model.MapDetailsExpanded = true;
            var detail = Assert.Single(model.MapDetails); Assert.Equal(map, detail.FilePath);
            Assert.Equal(f.Signature, detail.Caption); Assert.Equal(MapSigningStatus.Signed, detail.Status);
            var row = Assert.Single(model.Projects); var target = Path.Combine(row.Destination, Path.GetFileName(map));
            Assert.Equal(await File.ReadAllBytesAsync(map, Token), await File.ReadAllBytesAsync(target, Token));
            if (hans) Assert.Contains(f.Signature, await File.ReadAllTextAsync(target, Token));
            else { using var pdf = UglyToad.PdfPig.PdfDocument.Open(target); Assert.Contains("БЛЦБ-009337", pdf.GetPage(1).Text); }
            var backup = Assert.Single(Directory.EnumerateFiles(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder), Path.GetFileName(map), SearchOption.AllDirectories));
            Assert.Equal(original, await File.ReadAllBytesAsync(backup, Token));
            var sourceTime = File.GetLastWriteTimeUtc(map); var targetTime = File.GetLastWriteTimeUtc(target);
            await model.RefreshAsync(); Assert.True(model.CanStartAutomatically);
            Assert.False(model.HasMapDetails); Assert.Empty(model.MapDetails); Assert.Empty(model.MapSigningSummary);
            await model.CopyAsync(); Assert.True(model.TextCopied, model.Error);
            Assert.Equal("Карты: обновлено файлов 0, без изменений 1.", model.MapSigningSummary);
            model.MapDetailsExpanded = true;
            detail = Assert.Single(model.MapDetails); Assert.Equal(MapSigningStatus.Unchanged, detail.Status); Assert.Equal(f.Signature, detail.Caption);
            Assert.Equal(2, copies); Assert.Equal(2, marks);
            Assert.Equal(sourceTime, File.GetLastWriteTimeUtc(map)); Assert.Equal(targetTime, File.GetLastWriteTimeUtc(target));
        }
        finally { if (Directory.Exists(shop)) Directory.Delete(shop, true); }
    }

    [Fact]
    public async Task SelectedForeignMapStopsWholeTransferAndUnselectedDocumentIsNeverOpened()
    {
        using var f = new MapSigningFixture(); f.Create();
        await File.WriteAllTextAsync(Path.Combine(f.Root, "Лист1.cnc"), "UP", Token);
        var foreign = f.Create("Чужая.pdf", native: "Чужой.rcam");
        var shop = f.Root + "-shop"; var copies = 0; var marks = 0;
        try
        {
            var model = new WorkflowShopTransferViewModel(shop, "db", f.Order, f.Book, _ => Task.CompletedTask,
                _ => { copies++; return Task.CompletedTask; }, recordHandoff: (_, _) => { marks++; return Task.CompletedTask; });
            await model.RefreshAsync(); var row = Assert.Single(model.Projects);
            var choice = Assert.Single(row.Files, c => c.File.SourcePath == foreign);
            choice.IsSelected = true; row.Reviewed = true;
            await model.CopyAsync(); Assert.False(model.AllPrepared); Assert.False(model.TextCopied); Assert.False(Directory.Exists(shop));
            Assert.Contains("не является картой", model.Error); Assert.False(model.CanCopy);
            Assert.Equal(0, copies); Assert.Equal(0, marks);

            // Explicitly rescan after a failed/partially signed action; skip the unrelated PDF.
            using var locked = File.Open(foreign, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await model.RefreshAsync(); row = Assert.Single(model.Projects);
            Assert.False(Assert.Single(row.Files, c => c.File.SourcePath == foreign).IsSelected);
            row.Reviewed = true;
            await model.CopyAsync(); Assert.True(model.AllPrepared, model.Error);
            Assert.True(model.TextCopied, model.Error); Assert.Equal(1, copies); Assert.Equal(1, marks);
        }
        finally { if (Directory.Exists(shop)) Directory.Delete(shop, true); }
    }

    private sealed class Editor(Func<byte[], CancellationToken, Task<WordMapEdit?>> edit) : IWorkflowWordMapEditor
    {
        public Task<WordMapEdit?> PrepareAsync(byte[] source, string projectFileName, string caption, CancellationToken token) => edit(source, token);
    }

    [Fact]
    public async Task SelectedBatchStopsOnFirstProblemBeforeLaterFilesOrProjects()
    {
        using var f = new ShopFixture("HansLaser");
        File.Delete(Path.Combine(f.Source, "Карта.DOC"));
        await File.WriteAllBytesAsync(Path.Combine(f.Source, "01-good.DOC"), [1], Token);
        await File.WriteAllBytesAsync(Path.Combine(f.Source, "02-bad.DOC"), [2], Token);
        await File.WriteAllBytesAsync(Path.Combine(f.Source, "03-untouched.DOC"), [3], Token);
        var otherSource = Path.Combine(f.Root, "second"); Directory.CreateDirectory(otherSource);
        const string otherName = "6 mm 2026-11-03-120001--HANSLASER";
        var other = f.Project with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = otherName, NativePath = Path.Combine(otherSource, otherName + ".Dsp") };
        await File.WriteAllBytesAsync(other.NativePath!, [], Token);
        await File.WriteAllBytesAsync(Path.Combine(otherSource, "Карта.DOC"), [4], Token);
        await File.WriteAllTextAsync(Path.Combine(otherSource, "Лист.GNC"), "UP", Token);
        var order = f.Order with { Projects = [f.Project, other], Items = [.. f.Order.Items, new(other.ItemId, 2, other.Id)],
            Specifications = [.. f.Order.Specifications, f.Order.Specifications[0] with { Id = other.Id }] };
        var book = f.Book with { Orders = [order] }; var calls = new List<byte>();
        var editor = new Editor((bytes, _) =>
        {
            calls.Add(bytes[0]);
            if (bytes[0] == 2) throw new IOException("Test failure");
            return Task.FromResult<WordMapEdit?>(new(null, 1, 0));
        });
        var plans = await WorkflowShopDiscovery.DiscoverAsync(f.ShopRoot, order, book, Token, editor);
        var results = await WorkflowMapSigning.SignSelectedAsync(order, book, plans.Select(p => ShopFixture.Choose(p)).ToArray(),
            _ => Task.CompletedTask, Token, editor);
        Assert.Equal(new byte[] { 1, 2 }, calls); Assert.Equal(2, results.Length);
        Assert.Equal(MapSigningStatus.Unchanged, results[0].Status); Assert.True(results[1].NeedsAttention);
        Assert.All(results, r => Assert.Equal(f.Project.Id, r.ProjectId));
        Assert.Equal(new byte[] { 3 }, await File.ReadAllBytesAsync(Path.Combine(f.Source, "03-untouched.DOC"), Token));
        Assert.Equal(new byte[] { 4 }, await File.ReadAllBytesAsync(Path.Combine(otherSource, "Карта.DOC"), Token));
        Assert.False(Directory.Exists(Path.Combine(f.Source, WorkflowMapSigning.BackupFolder)));
        Assert.False(Directory.Exists(f.ShopRoot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingWordOrBlcbNeverCopiesUnsignedMaps(bool missingBlcb)
    {
        using var f = new ShopFixture("HansLaser");
        var order = missingBlcb ? f.Order with { Blcb = null } : f.Order;
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "db", order, f.Book with { Orders = [order] }, _ => Task.CompletedTask,
            _ => throw new Exception("Must not copy"), wordMapEditor: new Editor((_, _) => throw new WordMapUnavailableException("Microsoft Word не установлен.")));
        // Missing BLCB may be rejected by destination planning even before signing.
        await model.RefreshAsync();
        if (model.CanCopy) await model.CopyAsync();
        Assert.False(model.AllPrepared); Assert.False(model.TextCopied); Assert.NotNull(model.Error); Assert.False(Directory.Exists(f.ShopRoot));
    }

    [Fact]
    public async Task CancellationDuringSecondMapKeepsFirstSignatureButPublishesNothingAndRequiresRescan()
    {
        using var f = new ShopFixture("HansLaser");
        await File.WriteAllTextAsync(Path.Combine(f.Source, "Вторая.DOC"), "second", Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var editor = new Editor(async (bytes, token) =>
        {
            if (++calls == 2) { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); }
            return new WordMapEdit([.. bytes, 42], 1, 1);
        });
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "db", f.Order, f.Book, _ => Task.CompletedTask,
            _ => throw new Exception("Must not copy"), wordMapEditor: editor);
        await model.RefreshAsync(); var action = model.CopyAsync(); await entered.Task.WaitAsync(Token);
        Assert.True(model.IsBusy); Assert.True(model.CanCancel); Assert.False(model.CanCopy);
        await model.CopyAsync(); Assert.Equal(2, calls);
        model.Cancel(); await action;
        Assert.False(model.IsBusy); Assert.True(model.WasCancelled); Assert.False(model.AllPrepared); Assert.False(model.CanCopy);
        Assert.False(Directory.Exists(f.ShopRoot)); Assert.Single(Directory.EnumerateDirectories(Path.Combine(f.Source, WorkflowMapSigning.BackupFolder)));
        await model.RefreshAsync(); Assert.True(model.CanCopy);
    }

    [Fact]
    public async Task ChangedProgramBeforeSigningStopsBeforeAnyMapOrDestinationWrite()
    {
        using var f = new ShopFixture("HansLaser"); var editor = new ShopWordEditor();
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "db", f.Order, f.Book, _ => Task.CompletedTask,
            _ => throw new Exception("Must not copy"), wordMapEditor: editor);
        await model.RefreshAsync(); await File.WriteAllTextAsync(Path.Combine(f.Source, "Лист.GNC"), "new", Token);
        await model.CopyAsync(); Assert.False(model.AllPrepared); Assert.NotNull(model.Error); Assert.Equal(0, editor.Calls);
        Assert.False(Directory.Exists(Path.Combine(f.Source, WorkflowMapSigning.BackupFolder))); Assert.False(Directory.Exists(f.ShopRoot));
    }

    [Fact]
    public async Task SourceChangeAfterSignatureCheckCannotBeAdoptedAndCopied()
    {
        using var f = new ShopFixture("HansLaser"); var checkedSignature = false;
        var editor = new Editor((_, _) => { checkedSignature = true; return Task.FromResult<WordMapEdit?>(new(null, 1, 0)); });
        var model = new WorkflowShopTransferViewModel(f.ShopRoot, "db", f.Order, f.Book, async token =>
        {
            if (checkedSignature) await File.WriteAllTextAsync(Path.Combine(f.Source, "Карта.DOC"), "Changed after editor read", token);
        }, _ => throw new Exception("Must not copy"), wordMapEditor: editor);
        await model.RefreshAsync(); await model.CopyAsync();
        Assert.False(model.AllPrepared); Assert.NotNull(model.Error); Assert.False(Directory.Exists(f.ShopRoot));
    }

    [Fact]
    public async Task SignatureOnlyCorrectionCanUpdateAnUnchangedOwnedDestinationWithoutAnotherConfirmation()
    {
        using var f = new ShopFixture("HansLaser");
        var first = new WorkflowShopTransferViewModel(f.ShopRoot, "db", f.Order, f.Book, _ => Task.CompletedTask, _ => Task.CompletedTask, wordMapEditor: new ShopWordEditor());
        await first.RefreshAsync(); await first.CopyAsync(); Assert.True(first.AllPrepared, first.Error);
        var spec = f.Order.Specifications[0];
        var order = f.Order with { Specifications = [spec with { Current = spec.Current with { DocumentNumber = "1102-123456" } }] };
        var changed = new WorkflowShopTransferViewModel(f.ShopRoot, "db", order, f.Book with { Orders = [order] }, _ => Task.CompletedTask, _ => Task.CompletedTask, wordMapEditor: new ShopWordEditor());
        await changed.RefreshAsync(); Assert.True(changed.CanStartAutomatically); Assert.False(changed.AllowReplacement);
        await changed.CopyAsync(); Assert.True(changed.AllPrepared, changed.Error);
        Assert.Contains("1102-123456", await File.ReadAllTextAsync(Path.Combine(changed.Projects[0].Destination, "Карта.DOC"), Token));
    }
}
