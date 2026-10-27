using Jornal.Workflow.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class ProjectPreparationMigrationTests
{
    [Fact]
    public async Task SchemaThreeUpgradeBacksUpPlanningFieldsAndPreservesProjectsVariantsAndSpecificationHistory()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var first = await f.AddAsync();
        await ProjectFixture.WriteAsync(first); await f.AcceptAsync();
        f.Model.SelectedDraft!.Comment = "Сохранённая правка"; f.Model.SelectedDraft.HasBending = true;
        await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var alternative = await f.AddAsync("Плазма", redo: true);
        await ProjectFixture.WriteAsync(alternative, 20000); await f.AcceptAsync();
        await f.AddAsync(); // A separate project without a report must remain uncalculated.
        var before = await f.Store.ReadAsync(TestContext.Current.CancellationToken);
        var legacy = WorkflowTestData.LegacyJson(before, 3);
        foreach (var project in legacy["Orders"]![0]!["Projects"]!.AsArray())
        {
            project!["Material"] = "Прежний ввод при подготовке";
            project["MaterialGroup"] = "Нержавеющая сталь";
        }
        var path = Path.Combine(f.DirectoryPath, "schema-three.json"); var original = legacy.ToJsonString();
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var migrated = await new LocalWorkflowWorkspaceStore(path).ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(WorkflowWorkspace.CurrentSchema, migrated.SchemaVersion); Assert.Equal(before.Version, migrated.Version);
        Assert.Equal(before.Machines, migrated.Machines); Assert.Equal(before.Rates, migrated.Rates);
        var oldOrder = before.Orders[0]; var order = migrated.Orders[0];
        Assert.Equal(oldOrder.Version, order.Version); Assert.Equal(oldOrder.Items, order.Items);
        Assert.Equal(oldOrder.Projects, order.Projects);
        Assert.Equal(oldOrder.CuttingTotal, order.CuttingTotal); Assert.Equal(1, order.PendingProjectCount);
        Assert.Equal(first.Id, order.Items[0].CurrentProjectId);
        foreach (var specification in oldOrder.Specifications)
        {
            var after = order.Specifications.Single(s => s.Id == specification.Id);
            Assert.Equal(specification.Current, after.Current);
            Assert.Equal("Ст3", after.Current.Parameters.Material); Assert.Equal("Базовая", after.Current.Parameters.MaterialGroup);
            Assert.True(File.Exists(after.Current.Report!.ArchivePath));
        }
        foreach (var project in order.Projects)
        {
            Assert.True(File.Exists(project.NativePath));
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(project));
            Assert.False(json.RootElement.TryGetProperty("Material", out _));
            Assert.False(json.RootElement.TryGetProperty("MaterialGroup", out _));
        }
        var backup = Assert.Single(Directory.GetFiles(f.DirectoryPath, "schema-three.json.*.bak"));
        Assert.Equal(original, await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken));
        Assert.Contains("Прежний ввод при подготовке", JsonNode.Parse(await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken))!["Orders"]![0]!["Projects"]![0]!["Material"]!.GetValue<string>());
    }
}
