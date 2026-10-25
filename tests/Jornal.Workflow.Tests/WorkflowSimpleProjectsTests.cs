using System.Text.Json;
using Jornal.Workflow.Core;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowSimpleProjectsTests
{
    [Fact]
    public async Task ExistingCalculationWithoutProjectStaysAccessibleWithoutFabricatingFilesOrBreakingInclusion()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        var book = await f.Store.ReadAsync(TestContext.Current.CancellationToken); var order = book.Orders.Single();
        var saved = order.Specifications.Single().Current; var id = Guid.NewGuid();
        var source = Path.Combine(f.DirectoryPath, "previously-imported.html");
        await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.SaveSpecification(w, order.Id, order.Version,
            id, saved.Parameters, "Сохранённый расчёт", saved.DocumentDate, saved.AuthorId, DateTimeOffset.UtcNow, source), TestContext.Current.CancellationToken);
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var legacy = f.Model.Projects.Single(p => p.Project.Id == id); Assert.True(legacy.IsLegacyCalculation);
        Assert.False(legacy.CanToggle); Assert.False(legacy.HasProjectActions); Assert.False(legacy.CanChooseForTotal);
        Assert.False(legacy.CanEdit); Assert.False(legacy.CanTrash);
        Assert.Equal(f.DirectoryPath, legacy.FolderPath); Assert.Equal("Без проекта", legacy.Position);
        f.Model.SelectedProject = legacy; Assert.Equal(id, f.Model.SelectedDraft!.Id); Assert.True(f.Model.ShowProjectCalculation);
        f.Model.SelectedDraft.Coefficient = "1,2"; await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var selected = f.Model.SelectedProject;
        await f.Model.SetProjectIncludedAsync(project.Id, false); Assert.Null(f.Model.Error);
        Assert.Same(selected, f.Model.SelectedProject); Assert.Equal(id, f.Model.SelectedDraft!.Id);
        var after = await f.Store.ReadAsync(TestContext.Current.CancellationToken); Assert.Equal(11, after.SchemaVersion);
        Assert.Single(after.Orders.Single().Projects); Assert.Equal(project.Id, after.Orders.Single().Projects.Single().Id);
        Assert.Equal(2, after.Orders.Single().Specifications.Length); Assert.Equal(1326m, after.Orders.Single().CuttingTotal);
        Assert.False(File.Exists(source)); Assert.True(File.Exists(project.NativePath));
        Assert.DoesNotContain("IsLegacyCalculation", JsonSerializer.Serialize(after));
    }
}
