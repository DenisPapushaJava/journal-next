using System.Text.Json;
using System.Text.Json.Nodes;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowBlcbTests
{
    [Fact]
    public async Task NumberAloneCanBeSavedSearchedReopenedAndClearedWithoutExtraVersions()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var original = before.Orders[0];
        f.Model.Blcb = " 00017-А/5 "; await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        Assert.False(f.Model.HasPendingEdits);
        var saved = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = saved.Orders[0];
        Assert.Equal("00017-А/5", order.Blcb); Assert.Equal(original.Version + 1, order.Version);
        Assert.Equal("00017-А/5", f.Model.Orders[0].Blcb);
        Assert.DoesNotContain("BlcbYear", JsonSerializer.Serialize(saved));
        await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        Assert.Equal(saved.Version, (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Version);
        f.Model.Search = "0017-А"; Assert.Single(f.Model.Orders); Assert.EndsWith("00017-А/5", f.Model.HeaderSummary.Trim());
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Equal(order.Blcb, f.Model.Blcb);
        f.Model.ClearSearchCommand.Execute(null); f.Model.Blcb = " "; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        Assert.Null(f.Model.Error); Assert.False(f.Model.HasPendingEdits);
        order = (await f.Store.ReadAsync(TestContext.Current.CancellationToken)).Orders[0];
        Assert.Null(order.Blcb);
    }

    [Theory]
    [InlineData(8, 1987)] [InlineData(8, null)] [InlineData(7, 1987)]
    public async Task OldYearIsRemovedWithOneOriginalBackupAndNoOtherChanges(int schema, int? oldYear)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        f.Model.Blcb = "00017-А/5"; await f.Model.SaveHeaderCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var report = before.Orders[0].Specifications[0].Current.Report!;
        var reportBytes = await File.ReadAllBytesAsync(report.ArchivePath, TestContext.Current.CancellationToken);
        var node = WorkflowTestData.LegacyJson(before, schema);
        node["Orders"]![0]!["BlcbYear"] = oldYear;
        var original = node.ToJsonString(); var path = Path.Combine(f.DirectoryPath, "old-blcb.json");
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var store = new LocalWorkflowWorkspaceStore(path); var after = await store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowWorkspace.CurrentSchema, after.SchemaVersion); Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal("00017-А/5", after.Orders[0].Blcb);
        var persisted = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("BlcbYear", persisted);
        Assert.False(new WorkflowOrderSearchIndex(after).Documents[after.Orders[0].Id].Matches(new("1987"), out _));
        var backup = Assert.Single(Directory.GetFiles(f.DirectoryPath, "old-blcb.json.*.bak"));
        Assert.Equal(original, await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
        Assert.Equal(reportBytes, await File.ReadAllBytesAsync(report.ArchivePath, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(project.NativePath));
        await store.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Single(Directory.GetFiles(f.DirectoryPath, "old-blcb.json.*.bak"));
        Assert.Equal(persisted, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }
}
