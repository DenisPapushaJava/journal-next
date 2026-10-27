using Avalonia.Headless.XUnit;
using Jornal.Parsing.Documents;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowShopAutomationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task HansUsesAllRootGncDocWithArbitraryNamesAndMissingMetadata()
    {
        using var f = new ShopFixture("HansLaser", true);
        await File.WriteAllTextAsync(Path.Combine(f.Source, "Другая карта.Gnf.DOC"), "unfilled fields", Token);
        await File.WriteAllTextAsync(Path.Combine(f.Source, "Ещё одна карта.DOC"), "unfilled fields too", Token);
        await File.WriteAllTextAsync(Path.Combine(f.Source, "Произвольное имя 123.GNC"), "UP2", Token);
        await File.WriteAllTextAsync(Path.Combine(f.Source, "Рисунок.pdf"), "do not copy", Token);
        Directory.CreateDirectory(Path.Combine(f.Source, "архив")); await File.WriteAllTextAsync(Path.Combine(f.Source, "архив", "Old.GNC"), "do not copy", Token);
        var plan = Assert.Single(await f.Discover()); Assert.False(plan.NeedsReview); Assert.Equal(5, plan.Files.Length);
        Assert.All(plan.Files, file => Assert.True(file.Suggested));
        var result = Assert.Single(await f.Copy(new(plan, plan.Files, false, null))); Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(plan.Destination, "Другая карта.Gnf.DOC")));
        Assert.False(File.Exists(Path.Combine(plan.Destination, "Рисунок.pdf"))); Assert.False(Directory.Exists(Path.Combine(plan.Destination, "архив")));
    }

    [Fact]
    public async Task AnotherNativeProjectAndEmptyCandidatesStillNeedAttention()
    {
        using var f = new ShopFixture("HansLaser");
        await File.WriteAllTextAsync(Path.Combine(f.Source, "Другой.rcam"), "another project", Token);
        var plan = Assert.Single(await f.Discover()); Assert.True(plan.NeedsReview); Assert.NotNull(plan.ReviewReason);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => f.Copy(new(plan, plan.Files, false, null)));
        File.Delete(Path.Combine(f.Source, "Другой.rcam"));
        await File.WriteAllTextAsync(Path.Combine(f.Source, "Лист.GNC"), "", Token);
        plan = Assert.Single(await f.Discover()); Assert.True(plan.NeedsReview); Assert.Contains(plan.Files, file => !file.Suggested);
    }

    [Fact]
    public async Task NewCandidateAfterScanStopsCopyBeforeCreatingDestination()
    {
        using var f = new ShopFixture("HansLaser"); var plan = Assert.Single(await f.Discover());
        await File.WriteAllTextAsync(Path.Combine(f.Source, "new.GNC"), "new program", Token);
        var result = Assert.Single(await f.Copy(new(plan, plan.Files, false, null)));
        Assert.False(result.Success); Assert.Contains("Состав рабочей папки", result.Message); Assert.False(Directory.Exists(f.ShopRoot));
    }

    [AvaloniaFact]
    public async Task RepeatIsAutomaticButChangedDeliveredFilesRequireExplicitReplacement()
    {
        using var f = new ShopFixture("HansLaser"); var copied = 0;
        WorkflowShopTransferViewModel Model() => new(f.ShopRoot, "test-db", f.Order, f.Book, _ => Task.CompletedTask, _ => { copied++; return Task.CompletedTask; });
        var first = Model(); await first.RefreshAsync(); Assert.True(first.CanStartAutomatically); await first.CopyAsync();
        Assert.True(first.AllPrepared); Assert.True(first.TextCopied); Assert.Equal(1, copied);
        var path = Path.Combine(first.Projects[0].Destination, "Лист.GNC"); var modified = File.GetLastWriteTimeUtc(path);
        var repeat = Model(); await repeat.RefreshAsync(); Assert.True(repeat.CanStartAutomatically); Assert.False(repeat.HasExisting); await repeat.CopyAsync();
        Assert.True(repeat.AllPrepared); Assert.Equal(modified, File.GetLastWriteTimeUtc(path)); Assert.Equal(2, copied);
        await File.WriteAllTextAsync(Path.Combine(f.Source, "Лист.GNC"), "new UP", Token);
        var changed = Model(); await changed.RefreshAsync(); Assert.False(changed.CanStartAutomatically); Assert.True(changed.ShowReplacement); Assert.False(changed.CanCopy);
        await changed.CopyAsync(); Assert.Equal("UP bytes", await File.ReadAllTextAsync(path, Token)); Assert.Equal(2, copied);
        changed.AllowReplacement = true; Assert.True(changed.CanCopy); Assert.False(changed.CanStartAutomatically);
        await changed.CopyAsync(); Assert.True(changed.AllPrepared); Assert.False(changed.ShowReplacement); Assert.Equal(3, copied);
        Assert.Equal("new UP", await File.ReadAllTextAsync(path, Token));
    }

    [Fact]
    public async Task PreflightChecksAllProjectsBeforeCopyingAnyOfThem()
    {
        using var f = new ShopFixture("HansLaser"); var folder = Path.Combine(f.Root, "second"); Directory.CreateDirectory(folder);
        foreach (var path in Directory.EnumerateFiles(f.Source)) File.Copy(path, Path.Combine(folder, Path.GetFileName(path)));
        var other = f.Project with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), Name = "Другой проект", NativePath = Path.Combine(folder, "рабочий.rcam") };
        var order = f.Order with { Projects = [f.Project, other], Items = [f.Order.Items[0], new(other.ItemId, 2, other.Id)],
            Specifications = [f.Order.Specifications[0], f.Order.Specifications[0] with { Id = other.Id }] };
        var plans = await WorkflowShopDiscovery.DiscoverAsync(f.ShopRoot, order, f.Book, Token);
        File.Delete(Path.Combine(folder, "Лист.GNC"));
        var results = await WorkflowShopTransfer.CopyAsync(f.ShopRoot, "test-db", order.Id, plans.Select(p => new ShopTransferSelection(p, p.Files, false, null)).ToArray(), false, _ => Task.CompletedTask, token: Token);
        var failure = Assert.Single(results); Assert.False(failure.Success); Assert.Equal(other.Id, failure.ProjectId); Assert.False(Directory.Exists(f.ShopRoot));
    }

    [Fact]
    public void RecognizedUnrelatedDocumentsDoNotMakeAnOtherwiseCompletePlanAmbiguous()
    {
        static SourceDocument Pdf(params string[] words) => new(DocumentKind.Pdf, null, [], [new(1, 600, 800, words.Select((text, i) => new PdfWord(text, 10 + i * 20, 750, 20 + i * 20, 760)).ToArray())]);
        Assert.True(WorkflowShopDiscovery.IsBankDetails(Pdf("Банковские", "реквизиты", "ИНН", "БИК")));
        Assert.False(WorkflowShopDiscovery.IsBankDetails(Pdf("Реквизиты", "ИНН", "БИК", "CNC")));
        Assert.False(WorkflowShopDiscovery.IsBankDetails(Pdf("Реквизиты", "ИНН")));
        using var f = new ShopFixture(); var file = new ShopFile("чужой.pdf", "path", 1, "hash", ShopFileKind.Map, false, "Реквизиты") { Ignored = true };
        var plan = new ShopProjectFiles(f.Project, "target", [file], null, null); Assert.False(plan.NeedsReview);
        var choice = new WorkflowShopProjectChoice(plan, null, null); Assert.False(choice.RequiresReview);
        choice.Files[0].IsSelected = true; Assert.True(choice.RequiresReview);
    }

    [Fact]
    public void ShopPathForExcludedVariantIsPureAndKeepsFullProjectName()
    {
        using var f = new ShopFixture("HansLaser");
        var order = f.Order with { Items = [f.Order.Items[0] with { IsIncluded = false }] };
        var path = WorkflowShopFolderPaths.ForProject(f.ShopRoot, order, f.Project.Id, f.Book.Employees, f.Book.Customers, f.Book.Machines);
        Assert.Equal(f.Project.Name, Path.GetFileName(path.Path)); Assert.Contains("БЛЦБ-009337", path.Path);
        Assert.False(Directory.Exists(f.ShopRoot)); Assert.False(order.Items[0].IsIncluded);
        Assert.Throws<WorkflowValidationException>(() => WorkflowShopFolderPaths.ForProject(f.ShopRoot, order with { Projects = [f.Project with { DeletedAt = DateTimeOffset.Now }] },
            f.Project.Id, f.Book.Employees, f.Book.Customers, f.Book.Machines));
    }

    [Fact]
    public async Task OpeningChecksOwnershipButAllowsInspectingExternallyEditedOwnFolder()
    {
        using var f = new ShopFixture("HansLaser"); var plan = Assert.Single(await f.Discover());
        Assert.False(await WorkflowShopTransfer.HasOwnedDestinationAsync(plan, "test-db", f.Order.Id, Token));
        Assert.False(Directory.Exists(f.ShopRoot));
        Assert.True(Assert.Single(await f.Copy(new(plan, plan.Files, false, null))).Success);
        await File.WriteAllTextAsync(Path.Combine(plan.Destination, "операторская заметка.txt"), "do not change", Token);
        Assert.True(await WorkflowShopTransfer.HasOwnedDestinationAsync(plan, "test-db", f.Order.Id, Token));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowShopTransfer.HasOwnedDestinationAsync(plan, "other-db", f.Order.Id, Token));
        await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowShopTransfer.InspectDestinationAsync(plan, "test-db", f.Order.Id, Token));
        Assert.Equal("do not change", await File.ReadAllTextAsync(Path.Combine(plan.Destination, "операторская заметка.txt"), Token));
    }

    [Fact]
    public async Task WordOwnerFilesAreNotMapsEvenWhenLockedOrCreatedAfterScan()
    {
        using var f = new ShopFixture("HansLaser", true);
        var owner = Path.Combine(f.Source, "~$Карта.DOC"); await File.WriteAllTextAsync(owner, "Office owner lock", Token);
        // The same name inside canonical bending is user content and must be copied.
        await File.WriteAllTextAsync(Path.Combine(f.Source, "ГИБКА", "~$Важный.DOC"), "keep inside bending", Token);
        using (File.Open(owner, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var plan = Assert.Single(await f.Discover()); Assert.False(plan.NeedsReview); Assert.Equal(2, plan.Files.Length);
            var later = Path.Combine(f.Source, "~$Другая.DOC"); await File.WriteAllTextAsync(later, "another lock", Token);
            var result = Assert.Single(await f.Copy(new(plan, plan.Files, false, null))); Assert.True(result.Success);
            Assert.False(File.Exists(Path.Combine(plan.Destination, "~$Карта.DOC")));
            Assert.True(File.Exists(Path.Combine(plan.Destination, "ГИБКА", "~$Важный.DOC")));
        }
    }
}
