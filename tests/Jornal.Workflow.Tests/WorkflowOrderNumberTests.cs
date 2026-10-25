using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowOrderNumberTests
{
    [Fact]
    public void SameSecondNumbersAreUniqueAndDoNotChangeWhenOrderDetailsAreEdited()
    {
        var at = DateTimeOffset.Parse("2026-10-24T14:35:27+03:00", CultureInfo.InvariantCulture);
        var book = WorkflowTestData.CreateWorkspace(); var actor = book.Employees[0].Id;
        book = WorkflowChanges.CreateOrder(book, "ПТЕК", actor, actor, actor, at, out var first);
        book = WorkflowChanges.CreateOrder(book, "Другой заказчик", actor, actor, actor, at.AddMilliseconds(123), out var second);
        book = WorkflowChanges.CreateOrder(book, "Третий заказчик", actor, actor, actor, at.AddSeconds(1), out var third);
        Assert.Equal("2026-10-24-143527", book.Orders.Single(o => o.Id == first).DisplayNumber);
        Assert.Equal("2026-10-24-143527-02", book.Orders.Single(o => o.Id == second).DisplayNumber);
        Assert.Equal("2026-10-24-143528", book.Orders.Single(o => o.Id == third).DisplayNumber);
        book = WorkflowChanges.UpdateOrder(book, first, 1, actor, actor, "БЛЦБ-7", actor, at.AddDays(5));
        Assert.Equal("2026-10-24-143527", book.Orders.Single(o => o.Id == first).DisplayNumber);
        Assert.Equal(1, book.Orders.Single(o => o.Id == first).Number);
        book.Validate();

        var duplicate = book with { Orders = book.Orders.Select(o => o.Id == second ? o with { ReferenceNumber = "2026-10-24-143527" } : o).ToArray() };
        Assert.Throws<WorkflowValidationException>(() => duplicate.Validate());
        foreach (var invalid in new[] { "", "З-0001", "2026-02-30-143527", "2026-10-24-246027", "2026-10-24-143527-01" })
            Assert.False(WorkflowOrderNumbers.IsValid(invalid));
        var used = new HashSet<string>(StringComparer.Ordinal);
        var last = "";
        for (var i = 0; i < 101; i++) last = WorkflowOrderNumbers.Allocate(used, at);
        Assert.Equal("2026-10-24-143527-101", last); Assert.True(WorkflowOrderNumbers.IsValid(last));
    }

    [Fact]
    public async Task SchemaFourNumbersAreAssignedOnceWithBackupAndPreserveAllSavedDataAndFiles()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var first = before.Orders.Single();
        before = WorkflowChanges.CreateOrder(before, "Вторая заявка", first.ManagerId, first.TechnologistId,
            first.TechnologistId, first.CreatedAt, out var secondId);
        var legacy = WorkflowTestData.LegacyJson(before, 4);
        foreach (var order in legacy["Orders"]!.AsArray()) order!.AsObject().Remove("ReferenceNumber");
        var path = Path.Combine(f.DirectoryPath, "schema-four.json"); var original = legacy.ToJsonString();
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var store = new LocalWorkflowWorkspaceStore(path);
        var migrated = await store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(11, migrated.SchemaVersion); Assert.Equal(before.Version, migrated.Version);
        var stem = first.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        Assert.Equal(stem, migrated.Orders.Single(o => o.Id == first.Id).ReferenceNumber);
        Assert.Equal(stem + "-02", migrated.Orders.Single(o => o.Id == secondId).ReferenceNumber);
        foreach (var old in before.Orders)
        {
            var after = migrated.Orders.Single(o => o.Id == old.Id);
            Assert.Equal(old.Number, after.Number); Assert.Equal(old.Version, after.Version);
            Assert.Equal(old.CreatedAt, after.CreatedAt); Assert.Equal(old.UpdatedAt, after.UpdatedAt);
            Assert.Equal(old.Items, after.Items); Assert.Equal(old.Projects, after.Projects);
            Assert.Equal(old.CuttingTotal, after.CuttingTotal);
            foreach (var spec in old.Specifications)
                Assert.Equal(spec.Current, after.Specifications.Single(s => s.Id == spec.Id).Current);
        }
        var revision = migrated.Orders.Single(o => o.Id == first.Id).Specifications.Single().Current;
        Assert.True(File.Exists(project.NativePath)); Assert.True(File.Exists(revision.SourcePath)); Assert.True(File.Exists(revision.Report!.ArchivePath));
        var backup = Assert.Single(Directory.GetFiles(f.DirectoryPath, "schema-four.json.*.bak"));
        Assert.Equal(original, await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
        var reopened = await store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(migrated.Orders.Select(o => o.ReferenceNumber), reopened.Orders.Select(o => o.ReferenceNumber));
        Assert.Single(Directory.GetFiles(f.DirectoryPath, "schema-four.json.*.bak"));
    }

    [Fact]
    public async Task AddingProjectAfterReferenceChangeKeepsExistingOrderFolderAndNativeNaming()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var first = await f.AddAsync();
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var order = book.Orders.Single() with { ReferenceNumber = "2026-01-01-010203" };
        var next = await WorkflowProjectFiles.PrepareAsync(book.ProjectRoot!, order, "ООО «Пример»",
            book.Machines.Single(m => m.Id == first.MachineId), 4, null, first.CreatedAt.AddSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(Path.GetDirectoryName(Path.GetDirectoryName(first.NativePath)), Path.GetDirectoryName(Path.GetDirectoryName(next.NativePath)));
        Assert.StartsWith("4 мм ", next.Name); Assert.EndsWith("--ЛАЗЕР", next.Name);
        Assert.True(File.Exists(first.NativePath)); Assert.True(File.Exists(next.NativePath));
    }
}
