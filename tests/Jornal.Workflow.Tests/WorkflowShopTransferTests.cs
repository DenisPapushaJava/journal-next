using System.Security.Cryptography;
using Jornal.Parsing.Documents;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

internal sealed class ShopFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "next-shop-" + Guid.NewGuid().ToString("N"));
    internal string ShopRoot => Path.Combine(Root, "shop");
    internal WorkflowWorkspace Book { get; }
    internal WorkflowOrder Order { get; }
    internal CuttingProject Project { get; }
    internal string Source => Path.GetDirectoryName(Project.NativePath!)!;
    internal ShopFixture(string machine = "Лазер", bool bending = false)
    {
        var book = WorkflowTestData.CreateWorkspace() with { Customers = [new(Guid.NewGuid(), "ЗПТО")] };
        var m = book.Machines.Single(m => m.Name == machine); var at = DateTimeOffset.Now;
        Directory.CreateDirectory(Path.Combine(Root, "source"));
        Project = new(Guid.NewGuid(), Guid.NewGuid(), 1, m.Id, 6, "6 мм 2026-10-26-101507--ЛАЗЕР", Path.Combine(Root, "source", "рабочий.rcam"), m.ImportProfile, at);
        File.WriteAllText(Project.NativePath!, "Проект не менять");
        var spec = new WorkflowSpecification(Project.Id, new("1026-101507", DateOnly.FromDateTime(at.Date), at, book.Employees[0].Id,
            new(m.Name, "Ст3", "Базовая", 6, "Лист", 1, 1, 1, null, null, 1, bending, null) { MachineId = m.Id }, new(Guid.NewGuid(), 1, 1, 1, 1, 2), null, m.ImportProfile));
        Order = new(Guid.NewGuid(), 1, book.Customers[0].Id, book.Employees[0].Id, book.Employees[1].Id, "009337", 1, at, at, [spec])
        { ReferenceNumber = "2026-10-26-101507", Projects = [Project], Items = [new(Project.ItemId, 1, Project.Id)] };
        Book = book with { Orders = [Order] };
        File.WriteAllText(Path.Combine(Source, machine == "HansLaser" ? "Лист.GNC" : machine == "Лазер" ? "Лист.cnc" : "Лист.nc"), "UP bytes");
        File.WriteAllText(Path.Combine(Source, machine == "HansLaser" ? "Карта.DOC" : "Карта.pdf"), "Map bytes");
        if (bending) Directory.CreateDirectory(Path.Combine(Source, "ГИБКА"));
    }
    internal Task<ShopProjectFiles[]> Discover() => WorkflowShopDiscovery.DiscoverAsync(ShopRoot, Order, Book, TestContext.Current.CancellationToken);
    internal static ShopTransferSelection Choose(ShopProjectFiles p, string? revision = null) => new(p, p.Files, true, revision);
    internal Task<ShopTransferResult[]> Copy(ShopTransferSelection selection, bool replace = false, Func<CancellationToken, Task>? validate = null, CancellationToken? token = null) =>
        WorkflowShopTransfer.CopyAsync(ShopRoot, "test-db", Order.Id, [selection], replace, validate ?? (_ => Task.CompletedTask), token: token ?? TestContext.Current.CancellationToken);
    public void Dispose() => Directory.Delete(Root, true);
}

public sealed class WorkflowShopTransferTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task CopiesOnlyChosenFilesAndWholeCanonicalBendingWithoutChangingSources()
    {
        using var f = new ShopFixture(bending: true);
        var bending = Path.Combine(f.Source, "ГИБКА"); Directory.CreateDirectory(Path.Combine(bending, "пустая"));
        Directory.CreateDirectory(Path.Combine(bending, "вложенная"));
        await File.WriteAllBytesAsync(Path.Combine(bending, ".скрытый.dxf"), [0, 255, 2, 0], Token);
        await File.WriteAllTextAsync(Path.Combine(bending, "вложенная", "рабочий.rcam"), "anything", Token);
        Directory.CreateDirectory(Path.Combine(f.Source, "материал", "Гибка"));
        await File.WriteAllTextAsync(Path.Combine(f.Source, "материал", "Гибка", "чужой.pdf"), "do not copy", Token);
        await File.WriteAllTextAsync(Path.Combine(f.Source, "цены.xlsx"), "do not copy", Token);
        var before = await ShopFileSystem.TreeAsync(f.Source, Token);
        var plan = Assert.Single(await f.Discover()); Assert.Null(plan.Problem); Assert.Equal(2, plan.Files.Length);
        Assert.False(Directory.Exists(f.ShopRoot));
        var result = Assert.Single(await f.Copy(ShopFixture.Choose(plan))); Assert.True(result.Success); Assert.False(result.Unchanged);
        Assert.True(ShopFileSystem.SameTree(before, await ShopFileSystem.TreeAsync(f.Source, Token)));
        Assert.Equal(new byte[] { 0, 255, 2, 0 }, await File.ReadAllBytesAsync(Path.Combine(plan.Destination, "ГИБКА", ".скрытый.dxf"), Token));
        Assert.True(Directory.Exists(Path.Combine(plan.Destination, "ГИБКА", "пустая")));
        Assert.True(File.Exists(Path.Combine(plan.Destination, "ГИБКА", "вложенная", "рабочий.rcam")));
        Assert.False(Directory.Exists(Path.Combine(plan.Destination, "материал"))); Assert.False(File.Exists(Path.Combine(plan.Destination, "цены.xlsx")));
        Assert.Equal(f.Project.Name, Path.GetFileName(plan.Destination));
        Assert.NotNull(await WorkflowShopTransfer.DestinationRevisionAsync(plan, "test-db", f.Order.Id, Token));
    }

    [Fact]
    public async Task RepeatSkipsIdenticalAndReplacementRemovesOnlyPreviouslyManagedFiles()
    {
        using var f = new ShopFixture(bending: true); var plan = Assert.Single(await f.Discover());
        var first = Assert.Single(await f.Copy(ShopFixture.Choose(plan))); Assert.True(first.Success);
        var second = Assert.Single(await f.Copy(ShopFixture.Choose(plan, first.DestinationRevision))); Assert.True(second.Unchanged);
        await File.WriteAllTextAsync(Path.Combine(f.Source, "Лист.cnc"), "changed UP", Token);
        File.Move(Path.Combine(f.Source, "Карта.pdf"), Path.Combine(f.Source, "Переименованная карта.pdf"));
        plan = Assert.Single(await f.Discover()); var selection = ShopFixture.Choose(plan, first.DestinationRevision);
        Assert.False(Assert.Single(await f.Copy(selection)).Success); Assert.True(File.Exists(Path.Combine(plan.Destination, "Карта.pdf")));
        var replacement = Assert.Single(await f.Copy(selection, true)); Assert.True(replacement.Success);
        Assert.False(File.Exists(Path.Combine(plan.Destination, "Карта.pdf")));
        Assert.True(File.Exists(Path.Combine(plan.Destination, "Переименованная карта.pdf")));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(plan.Destination)!, ".journal-next-*"));
    }

    [Fact]
    public async Task UnownedAndExternallyChangedTargetsAreNeverOverwritten()
    {
        using var f = new ShopFixture(); var plan = Assert.Single(await f.Discover());
        Directory.CreateDirectory(plan.Destination); await File.WriteAllTextAsync(Path.Combine(plan.Destination, "чужой.txt"), "important", Token);
        var failed = Assert.Single(await f.Copy(ShopFixture.Choose(plan), true)); Assert.False(failed.Success); Assert.Contains("без подтверждения", failed.Message);
        Directory.Delete(plan.Destination, true);
        var first = Assert.Single(await f.Copy(ShopFixture.Choose(plan))); Assert.True(first.Success);
        await File.WriteAllTextAsync(Path.Combine(plan.Destination, "Лист.cnc"), "operator edit", Token);
        failed = Assert.Single(await f.Copy(ShopFixture.Choose(plan, first.DestinationRevision), true)); Assert.False(failed.Success); Assert.Contains("вне программы", failed.Message);
        Assert.Equal("operator edit", await File.ReadAllTextAsync(Path.Combine(plan.Destination, "Лист.cnc"), Token));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ChangedSourceAndChangedBendingRejectEvenWithSameMtime(bool bendingChange)
    {
        using var f = new ShopFixture(bending: true); var plan = Assert.Single(await f.Discover());
        var file = Path.Combine(f.Source, "Лист.cnc"); var at = File.GetLastWriteTimeUtc(file);
        if (bendingChange) Directory.CreateDirectory(Path.Combine(f.Source, "ГИБКА", "добавлено"));
        else { await File.WriteAllTextAsync(file, "UP BYTES", Token); File.SetLastWriteTimeUtc(file, at); }
        Assert.False(Assert.Single(await f.Copy(ShopFixture.Choose(plan))).Success); Assert.False(Directory.Exists(plan.Destination));
    }

    [Fact]
    public async Task MissingBendingDoesNotFallBackToAnotherFolderAndEmptyBendingIsAllowed()
    {
        using var f = new ShopFixture(bending: true); Directory.Delete(Path.Combine(f.Source, "ГИБКА"));
        Directory.CreateDirectory(Path.Combine(f.Source, "материал", "ГИБКА"));
        Assert.NotNull(Assert.Single(await f.Discover()).Problem);
        Directory.CreateDirectory(Path.Combine(f.Source, "ГИБКА")); var plan = Assert.Single(await f.Discover());
        Assert.Empty(plan.Bending!.Files); Assert.True(Assert.Single(await f.Copy(ShopFixture.Choose(plan))).Success);
        Assert.True(Directory.Exists(Path.Combine(plan.Destination, "ГИБКА")));
    }

    [Fact]
    public async Task CancellationOrOrderConflictBeforePromotionKeepsPreviousCompleteTarget()
    {
        using var f = new ShopFixture(); var plan = Assert.Single(await f.Discover()); var first = Assert.Single(await f.Copy(ShopFixture.Choose(plan)));
        await File.WriteAllTextAsync(Path.Combine(f.Source, "Лист.cnc"), "new", Token); plan = Assert.Single(await f.Discover());
        var calls = 0;
        var result = Assert.Single(await f.Copy(ShopFixture.Choose(plan, first.DestinationRevision), true, _ =>
        { if (++calls == 2) throw new WorkflowValidationException("Заявка изменена"); return Task.CompletedTask; }));
        Assert.False(result.Success); Assert.Equal("UP bytes", await File.ReadAllTextAsync(Path.Combine(plan.Destination, "Лист.cnc"), Token));
        Assert.Empty(Directory.EnumerateDirectories(Path.GetDirectoryName(plan.Destination)!, ".journal-next-*"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Copy(ShopFixture.Choose(plan, first.DestinationRevision), token: cancellation.Token));
    }

    [Fact]
    public async Task LinkedDirectoriesAndLockedFilesFailWithoutPartialDestination()
    {
        using var f = new ShopFixture(bending: true);
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(Path.Combine(f.Source, "ГИБКА", "ссылка"), f.Root);
            Assert.NotNull(Assert.Single(await f.Discover()).Problem);
            Directory.Delete(Path.Combine(f.Source, "ГИБКА", "ссылка"));
        }
        var plan = Assert.Single(await f.Discover());
        using (File.Open(Path.Combine(f.Source, "Лист.cnc"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(Assert.Single(await f.Copy(ShopFixture.Choose(plan))).Success);
        Assert.False(Directory.Exists(plan.Destination));
    }

    [Fact]
    public async Task TargetAppearingAfterPreviewIsNotClaimedEvenIfOwned()
    {
        using var f = new ShopFixture(); var plan = Assert.Single(await f.Discover());
        Assert.True(Assert.Single(await f.Copy(ShopFixture.Choose(plan))).Success);
        Assert.False(Assert.Single(await f.Copy(ShopFixture.Choose(plan), true)).Success);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowShopTransfer.DestinationRevisionAsync(plan, "other-db", f.Order.Id, Token));
    }

    [Fact]
    public async Task ConcurrentClientCannotPromoteWhileAnotherClientOwnsTheDestinationLease()
    {
        using var f = new ShopFixture("HansLaser"); var plan = Assert.Single(await f.Discover());
        var promoting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var first = f.Copy(ShopFixture.Choose(plan), validate: async token =>
        { if (++calls == 2) { promoting.TrySetResult(); await release.Task.WaitAsync(token); } });
        await promoting.Task.WaitAsync(Token);
        try { Assert.False(Assert.Single(await f.Copy(ShopFixture.Choose(plan))).Success); }
        finally { release.TrySetResult(); }
        Assert.True(Assert.Single(await first).Success);
    }

    [Fact]
    public async Task PartialOrderKeepsFinishedProjectAndReportsTheFailedOne()
    {
        using var f = new ShopFixture("HansLaser"); var folder = Path.Combine(f.Root, "second"); Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(f.Source)) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        var other = f.Project with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Другой проект", NativePath = Path.Combine(folder, "рабочий.rcam") };
        var order = f.Order with { Projects = [f.Project, other], Items = [f.Order.Items[0], new(other.ItemId, 2, other.Id)],
            Specifications = [f.Order.Specifications[0], f.Order.Specifications[0] with { Id = other.Id }] };
        var plans = await WorkflowShopDiscovery.DiscoverAsync(f.ShopRoot, order, f.Book, Token);
        var calls = 0;
        var results = await WorkflowShopTransfer.CopyAsync(f.ShopRoot, "test-db", order.Id, plans.Select(p => ShopFixture.Choose(p)).ToArray(), false, async token =>
        { if (++calls == 2) await File.WriteAllTextAsync(Path.Combine(folder, "Лист.GNC"), "changed during first project publication", token); }, token: Token);
        Assert.Equal(2, results.Length); Assert.True(results[0].Success); Assert.False(results[1].Success);
        Assert.True(Directory.Exists(plans[0].Destination)); Assert.False(Directory.Exists(plans[1].Destination));
    }

    [Theory]
    [InlineData("ГАР", ".nc", ".pdf")] [InlineData("Плазма", ".nc", ".pdf")]
    public async Task ManualProfilesNeverSelectFilesBasedOnlyOnExtensions(string machine, string up, string map)
    {
        using var f = new ShopFixture(machine); var plan = Assert.Single(await f.Discover());
        Assert.Null(plan.Problem); Assert.All(plan.Files, file => Assert.False(file.Suggested));
        Assert.Contains(plan.Files, file => file.Name.EndsWith(up, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Files, file => file.Name.EndsWith(map, StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Copy(new(plan, plan.Files, false, null)));
    }

    [Fact]
    public void RuleUsesStableMachineIdentityAndNotCaptionOrImportProfile()
    {
        var known = WorkflowMachine.Defaults().Single(m => m.Name == "Лазер");
        Assert.Equal(WorkflowShopDiscovery.Rule.Laser, WorkflowShopDiscovery.MachineRule(known with { Name = "Другой заголовок" }));
        Assert.Equal(WorkflowShopDiscovery.Rule.Unknown, WorkflowShopDiscovery.MachineRule(new("Лазер", "block-html/v1") { Id = Guid.NewGuid() }));
        Assert.Equal(WorkflowShopDiscovery.Rule.Gas, WorkflowShopDiscovery.MachineRule(new("Любое имя", "block-html/v1") { Id = Guid.NewGuid(), LegacyName = "Газо-кислородная" }));
    }

    [Fact]
    public void CoordinateFieldsKeepCncSeparateFromProjectAndRejectWrongMachineOrProject()
    {
        using var f = new ShopFixture();
        var words = new[] { new PdfWord("Проэкт:", 10, 750, 50, 760), new("рабочий.rcam", 60, 750, 170, 760),
            new("CNC:", 300, 750, 330, 760), new("полное имя 123.cnc", 340, 750, 510, 760),
            new("Станок:", 10, 730, 50, 740), new("3-Лазер", 60, 730, 160, 740) };
        var pdf = new SourceDocument(DocumentKind.Pdf, null, [], [new(1, 600, 800, words)]);
        Assert.Equal(["полное имя 123.cnc"], WorkflowShopDiscovery.BlockReferences(pdf, f.Project, WorkflowShopDiscovery.Rule.Laser)!);
        Assert.Null(WorkflowShopDiscovery.BlockReferences(pdf, f.Project with { NativePath = "другой.rcam" }, WorkflowShopDiscovery.Rule.Laser));
        Assert.Null(WorkflowShopDiscovery.BlockReferences(pdf, f.Project, WorkflowShopDiscovery.Rule.Gas));
    }
}
