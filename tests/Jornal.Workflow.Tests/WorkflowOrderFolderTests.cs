using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowOrderFolderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Created = new(2026, 1, 1, 0, 5, 0, TimeSpan.FromHours(3));
    private static WorkflowOrder OnJanuaryFirst(WorkflowOrder order) => order with
    {
        ReferenceNumber = "2026-01-01-000500-02", CreatedAt = Created.ToUniversalTime(), Projects = [], Items = [], Specifications = []
    };
    private static string Parent(CuttingProject project) => Path.GetDirectoryName(Path.GetDirectoryName(project.NativePath))!;

    [Fact]
    public async Task FirstProjectAddedLaterUsesTheOrdersBusinessDateAndItsOwnCreationTime()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var book = await f.Store.ReadAsync(Token);
        var order = OnJanuaryFirst(book.Orders.Single()); var at = Created.AddDays(9);
        var machine = book.Machines.Single(m => m.Name == "Лазер");
        var project = await WorkflowProjectFiles.PrepareAsync(book.ProjectRoot!, order, "Заказчик", machine, 2.5m, null, at, Token);
        Assert.Equal(Path.Combine(book.ProjectRoot!, "2026", "01", "01",
            order.DisplayNumber + "-" + order.Id.ToString("N")[..8] + " Заказчик"), Parent(project));
        Assert.Equal("2,5 мм 2026-01-10-000500--ЛАЗЕР", project.Name); Assert.Equal(at, project.CreatedAt);
        Assert.Equal(project.Name + ".rcam", Path.GetFileName(project.NativePath)); Assert.Equal(0, new FileInfo(project.NativePath!).Length);
        Assert.False(Directory.Exists(Path.Combine(book.ProjectRoot!, "2025"))); // CreatedAt in UTC is still December 31.
        Assert.False(Directory.Exists(Path.Combine(book.ProjectRoot!, "2026", "01", "10")));
    }

    [Fact]
    public async Task LaterPositionsAndVariantsReuseTheWholePathAcrossRootCustomerAndReferenceChanges()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var book = await f.Store.ReadAsync(Token);
        var order = OnJanuaryFirst(book.Orders.Single()); var machine = book.Machines.Single(m => m.Name == "Лазер");
        var first = await WorkflowProjectFiles.PrepareAsync(book.ProjectRoot!, order, "Прежний заказчик", machine, 4, null, Created, Token);
        order = order with { Projects = [first], Items = [new(first.ItemId, 1, first.Id)] };
        var otherRoot = Path.Combine(f.DirectoryPath, "new-root");
        // Root reservations must remain in the old storage: even the same requested second cannot reuse a name.
        var second = await WorkflowProjectFiles.PrepareAsync(otherRoot, order with { ReferenceNumber = "2026-01-02-123456" },
            "Новое название", machine, 4, null, Created, Token);
        Assert.NotEqual(first.Name, second.Name); Assert.Equal(Created.AddSeconds(1), second.CreatedAt);
        order = order with { Projects = [second, first], Items = [.. order.Items, new(second.ItemId, 2, second.Id)] };
        var later = await WorkflowProjectFiles.PrepareAsync(otherRoot, order, "Другое название", machine, 6, null, Created.AddDays(9), Token);
        var variant = await WorkflowProjectFiles.PrepareAsync(otherRoot, order, "Другое название",
            book.Machines.Single(m => m.Name == "Плазма"), 8, first.ItemId, Created.AddYears(1), Token);
        Assert.Equal(first.ItemId, variant.ItemId); Assert.Equal(2, variant.Variant);
        Assert.All(new[] { second, later, variant }, p => Assert.Equal(Parent(first), Parent(p)));
        Assert.False(Directory.Exists(otherRoot)); Assert.Equal(4, Directory.GetFiles(Path.Combine(book.ProjectRoot!, ".workflow-names")).Length);
        Assert.True(File.Exists(first.NativePath)); Assert.True(File.Exists(second.NativePath));
        var freshOrder = OnJanuaryFirst(order) with { Id = Guid.NewGuid() };
        var fresh = await WorkflowProjectFiles.PrepareAsync(otherRoot, freshOrder, "Заказчик", machine, 4, null, Created, Token);
        Assert.StartsWith(otherRoot + Path.DirectorySeparatorChar, fresh.NativePath);
    }

    [Fact]
    public async Task LegacyScatteredFoldersAndTrashedProjectsRemainUntouchedAndChooseTheEarliestAnchor()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var book = await f.Store.ReadAsync(Token);
        var order = OnJanuaryFirst(book.Orders.Single()); var machine = book.Machines.Single(m => m.Name == "Лазер");
        var first = await WorkflowProjectFiles.PrepareAsync(book.ProjectRoot!, order, "Заказчик", machine, 4, null, Created, Token);
        var oldParent = Path.Combine(book.ProjectRoot!, "2026", "01", "10", Path.GetFileName(Parent(first)));
        var later = first with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), CreatedAt = Created.AddDays(9),
            NativePath = Path.Combine(oldParent, first.Name, first.Name + ".rcam") };
        Directory.CreateDirectory(Path.GetDirectoryName(later.NativePath)!);
        await File.WriteAllTextAsync(first.NativePath!, "Первый рабочий проект", Token);
        await File.WriteAllTextAsync(later.NativePath!, "Другой рабочий проект", Token);
        first = first with { DeletedAt = Created.AddDays(10), DeletedBy = order.TechnologistId };
        order = order with { Projects = [later, first], Items = [new(later.ItemId, 2, later.Id), new(first.ItemId, 1, first.Id)] };
        var next = await WorkflowProjectFiles.PrepareAsync(book.ProjectRoot!, order, "Новое название", machine, 6, null, Created.AddMonths(1), Token);
        Assert.Equal(Parent(first), Parent(next));
        Assert.Equal("Первый рабочий проект", await File.ReadAllTextAsync(first.NativePath!, Token));
        Assert.Equal("Другой рабочий проект", await File.ReadAllTextAsync(later.NativePath!, Token)); Assert.True(Directory.Exists(oldParent));
    }

    [Fact]
    public async Task ConcurrentReservationsCrossingMidnightStillUseOneOrderDirectory()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var book = await f.Store.ReadAsync(Token);
        var order = OnJanuaryFirst(book.Orders.Single()); var at = new DateTimeOffset(2026, 1, 10, 23, 59, 59, TimeSpan.FromHours(3));
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() => WorkflowProjectFiles.PrepareAsync(book.ProjectRoot!, order,
            "Заказчик", book.Machines[0], 4, null, at, Token), Token));
        var projects = await Task.WhenAll(tasks);
        Assert.Single(projects.Select(Parent).Distinct()); Assert.Equal(4, projects.Select(p => p.Name).Distinct().Count());
        Assert.Contains(projects, p => p.CreatedAt.Day == 11);
        Assert.All(projects, p => Assert.Contains(Path.Combine("2026", "01", "01"), Parent(p)));
        Assert.All(projects, p => Assert.Equal(0, new FileInfo(p.NativePath!).Length));
    }

    [Fact]
    public async Task UnrecognizedStoredLayoutDoesNotGuessAStorageRootOrCreateFilesElsewhere()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var book = await f.Store.ReadAsync(Token);
        var order = OnJanuaryFirst(book.Orders.Single());
        var project = new CuttingProject(Guid.NewGuid(), Guid.NewGuid(), 1, book.Machines[0].Id, 4, "Проект",
            Path.Combine(f.DirectoryPath, "arbitrary", "a", "b", "c", "order", "project", "file.rcam"), "block-html/v1", Created);
        order = order with { Projects = [project], Items = [new(project.ItemId, 1, project.Id)] };
        var unused = Path.Combine(f.DirectoryPath, "unused-root");
        var error = await Assert.ThrowsAsync<WorkflowValidationException>(() => WorkflowProjectFiles.PrepareAsync(unused, order,
            "Заказчик", book.Machines[0], 4, null, Created.AddDays(9), Token));
        Assert.Contains("определить папку заявки", error.Message);
        Assert.False(Directory.Exists(unused)); Assert.False(Directory.Exists(Path.Combine(f.DirectoryPath, "arbitrary")));
    }
}
