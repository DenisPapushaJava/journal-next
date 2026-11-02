using System.Globalization;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowProjectNameTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void FractionalNamesUseCommaForEveryMachineRegardlessOfCurrentCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var at = new DateTimeOffset(2026, 11, 1, 12, 34, 56, TimeSpan.FromHours(3));
            foreach (var machine in WorkflowMachine.Defaults())
            {
                var name = WorkflowProjectNames.Name(machine, 1.250m, at);
                var units = machine.ImportProfile == "hans-pdf/v1" ? "mm" : "мм";
                var prefix = machine.ImportProfile is null ? "" : "1,25 " + units + " ";
                Assert.Equal(prefix + "2026-11-01-123456--" + WorkflowProjectNames.Tag(machine), name);
                var integer = WorkflowProjectNames.Name(machine, 2m, at);
                Assert.Equal((machine.ImportProfile is null ? "" : "2 " + units + " ") +
                    "2026-11-01-123456--" + WorkflowProjectNames.Tag(machine), integer);
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("Лазер", "2.5")]
    [InlineData("Лазер", "2,5")]
    [InlineData("Плазма", "2.5")]
    [InlineData("Газо-кислородная", "2.5")]
    [InlineData("HansLaser", "2.5")]
    [InlineData("ГАР", "2.5")]
    public async Task CreationVariantsAndEmptyProjectRenameKeepFolderFileAndDatabaseNamesConsistent(string machineName, string input)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        f.Model.AddProjectCommand.Execute(null);
        var machine = f.Model.ProjectMachines.Single(m => m.Name == machineName);
        f.Model.PlanMachine = machine; f.Model.PlanThickness = input;
        await f.Model.CreateProjectCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var original = f.Model.SelectedProject!.Project;
        Assert.Equal(2.5m, original.ThicknessMm);
        AssertPaths(original, machine, "2,5");

        f.Model.RedoProjectCommand.Execute(null); f.Model.PlanThickness = "1.5";
        await f.Model.CreateProjectCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        var variant = f.Model.SelectedProject!.Project;
        Assert.Equal(original.ItemId, variant.ItemId); Assert.Equal(2, variant.Variant);
        Assert.Equal(1.5m, variant.ThicknessMm);
        AssertPaths(variant, machine, "1,5");

        var book = await f.Store.ReadAsync(Token); var order = Assert.Single(book.Orders);
        var changed = await new WorkflowProjectMaintenance(f.Store).RenameAsync(order.Id, order.Version, variant.Id,
            machine.Id, 1.25m, order.TechnologistId, Token);
        var renamed = Assert.Single(changed.Orders[0].Projects, p => p.Id == variant.Id);
        Assert.Equal(variant.CreatedAt, renamed.CreatedAt);
        Assert.Equal(variant.Name[(variant.Name.LastIndexOf("--", StringComparison.Ordinal) - 17)..],
            renamed.Name[(renamed.Name.LastIndexOf("--", StringComparison.Ordinal) - 17)..]);
        AssertPaths(renamed, machine, "1,25");
        Assert.False(Directory.Exists(Path.GetDirectoryName(variant.NativePath)));
        AssertPaths(original, machine, "2,5");
    }

    private static void AssertPaths(CuttingProject project, WorkflowMachine machine, string thickness)
    {
        Assert.StartsWith(thickness + (machine.ImportProfile == "hans-pdf/v1" ? " mm " : " мм "), project.Name);
        Assert.Equal(project.Name, Path.GetFileName(Path.GetDirectoryName(project.NativePath)));
        Assert.Equal(project.Name + WorkflowProjectNames.Extension(machine), Path.GetFileName(project.NativePath));
        Assert.Equal(0, new FileInfo(project.NativePath!).Length);
    }
}
