using System.Text.Json;
using System.Text.Json.Nodes;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowCurrentStateTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    public async Task LegacyHistoryBecomesOneCurrentCalculationWithOriginalBackupAndStableFiles(int schema)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var first = f.Model.SelectedDraft!.Saved!;
        f.Model.SelectedDraft.Length = "15"; f.Model.SelectedDraft.Coefficient = "1,2";
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var manual = f.Model.SelectedDraft.Saved!;
        await ProjectFixture.WriteAsync(project, tail: "<!-- corrected -->"); await f.AcceptAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = before.Orders.Single();
        var current = order.Specifications.Single().Current;
        var legacy = WorkflowTestData.LegacyJson(before, schema);
        var oldOrder = legacy["Orders"]![0]!; var spec = oldOrder["Specifications"]![0]!;
        var revisions = new JsonArray(State(first, 1), State(manual, 2), State(current, 3));
        revisions[0]!["DocumentNumber"] = "УСТАРЕВШАЯ-999";
        revisions[0]!["AuthorId"] = before.Employees[0].Id;
        spec["Revisions"] = revisions;
        if (schema <= 9)
            foreach (var employee in legacy["Employees"]!.AsArray()) employee!.AsObject().Remove("IsTechnologist");
        var path = Path.Combine(f.DirectoryPath, "history-workspace.json"); var original = legacy.ToJsonString();
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var archiveBytes = await File.ReadAllBytesAsync(current.Report!.ArchivePath, TestContext.Current.CancellationToken);
        var store = new LocalWorkflowWorkspaceStore(path); var after = await store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowWorkspace.CurrentSchema, after.SchemaVersion); Assert.Equal(before.Version, after.Version);
        var migrated = after.Orders.Single(); Assert.Equal(order.Id, migrated.Id); Assert.Equal(order.Version, migrated.Version);
        Assert.Equal(order.ReferenceNumber, migrated.ReferenceNumber); Assert.Equal(order.CreatedById, migrated.CreatedById);
        Assert.Equal(order.CreatedAt, migrated.CreatedAt); Assert.Equal(order.UpdatedAt, migrated.UpdatedAt);
        Assert.Equal(current, Assert.Single(migrated.Specifications).Current);
        Assert.Equal(order.CuttingTotal, migrated.CuttingTotal); Assert.Equal(order.ManagerId, migrated.ManagerId);
        Assert.Equal(order.TechnologistId, migrated.TechnologistId);
        if (schema > 2) { Assert.Equal(order.Items, migrated.Items); Assert.Equal(order.Projects, migrated.Projects); }
        Assert.True(after.Employees.Single(e => e.Id == before.Employees[0].Id).IsTechnologist);
        Assert.Equal(archiveBytes, await File.ReadAllBytesAsync(current.Report.ArchivePath, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(first.Report!.ArchivePath)); Assert.True(File.Exists(project.NativePath));
        var persisted = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("\"Events\"", persisted); Assert.DoesNotContain("\"Revisions\"", persisted); Assert.DoesNotContain("УСТАРЕВШАЯ-999", persisted);
        Assert.False(new WorkflowOrderSearchIndex(after).Documents[migrated.Id].Matches(new("УСТАРЕВШАЯ-999"), out _));
        var backup = Assert.Single(Directory.GetFiles(f.DirectoryPath, "history-workspace.json.*.bak"));
        Assert.Equal(original, await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
        await store.ReadAsync(TestContext.Current.CancellationToken); Assert.Single(Directory.GetFiles(f.DirectoryPath, "history-workspace.json.*.bak"));
        Assert.Equal(persisted, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

        static JsonObject State(SpecificationState state, int number)
        { var json = JsonSerializer.SerializeToNode(state)!.AsObject(); json["Id"] = Guid.NewGuid(); json["Number"] = number; return json; }
    }

    [Fact]
    public async Task RepeatedSavesReplaceCurrentStateAndKeepConflictGuardWithoutAuditArrays()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var first = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders.Single();
        for (var i = 1; i <= 5; i++)
        {
            f.Model.SelectedDraft!.Comment = "Правка " + i;
            await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        }
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var saved = book.Orders.Single();
        Assert.Equal("Правка 5", Assert.Single(saved.Specifications).Current.Parameters.Comment);
        Assert.Equal(first.Version + 5, saved.Version);
        var json = await File.ReadAllTextAsync(f.Store.FilePath, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("\"Events\"", json); Assert.DoesNotContain("\"Revisions\"", json); Assert.DoesNotContain("Правка 4", json);
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Equal(book.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => f.Store.WriteOrderAsync(first.Id, first.Version, w => w, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MalformedLegacyStateDoesNotRewriteOrBackUpAnInvalidWorkspace(bool empty)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var json = WorkflowTestData.LegacyJson(await f.Store.ReadAsync(TestContext.Current.CancellationToken), 10);
        json["Orders"]![0]!["Specifications"]![0]!["Revisions"] = empty ? new JsonArray() : new JsonArray((JsonNode?)null);
        var path = Path.Combine(f.DirectoryPath, "invalid-history.json"); var original = json.ToJsonString();
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<WorkflowValidationException>(() => new LocalWorkflowWorkspaceStore(path).ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(f.DirectoryPath, "invalid-history.json.*.bak"));
    }
}
