using System.Security.Cryptography;
using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class ProjectMaintenanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Лазер", 8, ".rcam")]
    [InlineData("HansLaser", 4, ".Dsp")]
    public async Task EmptyProjectRenamesDirectoryAndNativeFileWhileKeepingIdentityAndTime(string machine, int thickness, string extension)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var original = await f.AddAsync();
        var book = await f.Store.ReadAsync(Token); var order = book.Orders[0]; var target = book.Machines.Single(m => m.Name == machine);
        var changed = await new WorkflowProjectMaintenance(f.Store).RenameAsync(order.Id, order.Version, original.Id,
            target.Id, thickness, order.TechnologistId, Token);
        var project = Assert.Single(changed.Orders[0].Projects);
        Assert.Equal(original.Id, project.Id); Assert.Equal(original.ItemId, project.ItemId); Assert.Equal(original.CreatedAt, project.CreatedAt);
        Assert.Equal(original.Variant, project.Variant); Assert.Equal(thickness, project.ThicknessMm); Assert.Equal(target.Id, project.MachineId);
        Assert.Null(project.Rename); Assert.Equal(WorkflowProjectNames.Name(target, thickness, original.CreatedAt), project.Name);
        Assert.Equal(project.Name, Path.GetFileName(Path.GetDirectoryName(project.NativePath)));
        Assert.Equal(project.Name + extension, Path.GetFileName(project.NativePath));
        Assert.Equal(0, new FileInfo(project.NativePath!).Length);
        Assert.False(Directory.Exists(Path.GetDirectoryName(original.NativePath)));
        Assert.Single(Directory.GetFiles(Path.Combine(book.ProjectRoot!, ".workflow-names")));
        // The persistent ownership receipt permits a second correction, without fabricating another project.
        changed = await new WorkflowProjectMaintenance(f.Store).RenameAsync(order.Id, changed.Orders[0].Version,
            original.Id, target.Id, thickness + 1, order.TechnologistId, Token);
        Assert.Single(changed.Orders[0].Projects); Assert.Equal(thickness + 1, changed.Orders[0].Projects[0].ThicknessMm);
    }

    [Theory]
    [InlineData("filled")]
    [InlineData("report")]
    [InlineData("directory")]
    public async Task WorkingFilesCannotBeRenamedAndDoNotLeaveAnIntent(string kind)
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var folder = Path.GetDirectoryName(project.NativePath)!;
        if (kind == "filled") await File.WriteAllTextAsync(project.NativePath!, "Рабочий проект", Token);
        if (kind == "report") await ProjectFixture.WriteAsync(project);
        if (kind == "directory") Directory.CreateDirectory(Path.Combine(folder, "Чертежи"));
        var before = await f.Store.ReadAsync(Token); var order = before.Orders[0];
        var error = await Assert.ThrowsAsync<ProjectFileFailure>(() => new WorkflowProjectMaintenance(f.Store).RenameAsync(
            order.Id, order.Version, project.Id, project.MachineId, 8, order.TechnologistId, Token));
        Assert.Equal(ProjectFileFailureKind.Unsafe, error.Kind);
        var after = await f.Store.ReadAsync(Token); Assert.Equal(before.Version, after.Version);
        Assert.Null(after.Orders[0].Projects[0].Rename); Assert.True(File.Exists(project.NativePath));
    }

    [Fact]
    public async Task ExistingDestinationIsNeverOverwritten()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var before = await f.Store.ReadAsync(Token); var order = before.Orders[0];
        var plan = WorkflowProjects.ProposeRename(project, before.Machines.Single(m => m.Id == project.MachineId), 8, order.TechnologistId, DateTimeOffset.UtcNow);
        var target = Path.GetDirectoryName(plan.NativePath)!; Directory.CreateDirectory(target);
        var sentinel = Path.Combine(target, "чужой файл.txt"); await File.WriteAllTextAsync(sentinel, "Не менять", Token);
        var error = await Assert.ThrowsAsync<ProjectFileFailure>(() => new WorkflowProjectMaintenance(f.Store).RenameAsync(
            order.Id, order.Version, project.Id, project.MachineId, 8, order.TechnologistId, Token));
        Assert.Equal(ProjectFileFailureKind.Collision, error.Kind);
        Assert.Equal("Не менять", await File.ReadAllTextAsync(sentinel, Token)); Assert.True(File.Exists(project.NativePath));
        Assert.Equal(before.Version, (await f.Store.ReadAsync(Token)).Version);
    }

    [Fact]
    public async Task OpenNativeFileBlocksBeforeAnyIntentAndExplicitRetryWorksAfterItCloses()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var book = await f.Store.ReadAsync(Token); var order = book.Orders[0];
        using (var opened = new FileStream(project.NativePath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = await Assert.ThrowsAsync<ProjectFileFailure>(() => new WorkflowProjectMaintenance(f.Store).RenameAsync(
                order.Id, order.Version, project.Id, project.MachineId, 8, order.TechnologistId, Token));
            if (OperatingSystem.IsWindows()) Assert.Equal(ProjectFileFailureKind.Busy, failure.Kind);
            Assert.Equal(book.Version, (await f.Store.ReadAsync(Token)).Version);
        }
        var saved = await new WorkflowProjectMaintenance(f.Store).RenameAsync(order.Id, order.Version, project.Id,
            project.MachineId, 8, order.TechnologistId, Token);
        Assert.Equal(8, saved.Orders[0].Projects[0].ThicknessMm); Assert.Null(saved.Orders[0].Projects[0].Rename);
    }

    [Fact]
    public async Task SharingViolationAfterFileRenameHasDurableIntentAndCanResumeAfterReopening()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var before = await f.Store.ReadAsync(Token); var order = before.Orders[0]; var attempts = 0;
        var service = new WorkflowProjectMaintenance(f.Store, (_, _) =>
        {
            // Prove that filesystem work does not hold the workspace lease.
            using var lease = new FileStream(f.Store.FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            attempts++; throw new IOException("Synthetic Windows sharing violation", unchecked((int)0x80070020));
        });
        var failure = await Assert.ThrowsAsync<ProjectFileFailure>(() => service.RenameAsync(order.Id, order.Version,
            project.Id, project.MachineId, 8, order.TechnologistId, Token));
        Assert.Equal(ProjectFileFailureKind.Busy, failure.Kind); Assert.Equal(1, attempts);
        var interrupted = await f.Store.ReadAsync(Token); var old = interrupted.Orders[0]; var pending = old.Projects[0].Rename!;
        Assert.Equal(project.Name, old.Projects[0].Name); Assert.NotNull(pending);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(project.NativePath)!, Path.GetFileName(pending.NativePath))));
        Assert.Throws<WorkflowValidationException>(() => WorkflowProjects.SetTrash(interrupted, old.Id, old.Version, project.Id, true, old.TechnologistId, DateTimeOffset.UtcNow));
        Assert.Throws<WorkflowValidationException>(() => WorkflowChanges.MoveOrderToTrash(interrupted, old.Id, old.Version, old.TechnologistId, DateTimeOffset.UtcNow));
        await f.Model.ReloadCommand.ExecuteAsync(null); Assert.True(f.Model.HasPendingEdits);
        var editor = f.Model.CreateProjectEditor(project.Id)!; Assert.False(editor.CanEdit); Assert.Equal("8", editor.Thickness);
        await editor.SaveCommand.ExecuteAsync(null); Assert.True(editor.WasSaved); Assert.Null(editor.Error);
        var after = await f.Store.ReadAsync(Token); Assert.Null(after.Orders[0].Projects[0].Rename);
        Assert.True(File.Exists(after.Orders[0].Projects[0].NativePath)); Assert.False(f.Model.HasPendingEdits);
        Assert.Single(after.Orders[0].Projects);
    }

    [Fact]
    public async Task FailureAfterFolderMoveRecoversUsingOwnershipReceiptWithoutMovingAgain()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var book = await f.Store.ReadAsync(Token); var order = book.Orders[0]; FileStream? blockedCommit = null;
        try
        {
            var service = new WorkflowProjectMaintenance(f.Store, (source, target) =>
            {
                Directory.Move(source, target);
                blockedCommit = new(f.Store.FilePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            });
            await Assert.ThrowsAsync<ProjectFileFailure>(() => service.RenameAsync(order.Id, order.Version, project.Id,
                project.MachineId, 8, order.TechnologistId, Token));
        }
        finally { blockedCommit?.Dispose(); }
        book = await f.Store.ReadAsync(Token); order = book.Orders[0]; Assert.NotNull(order.Projects[0].Rename);
        Assert.False(Directory.Exists(Path.GetDirectoryName(project.NativePath)));
        var finished = await new WorkflowProjectMaintenance(f.Store, (_, _) => throw new InvalidOperationException("Must not move twice"))
            .RenameAsync(order.Id, order.Version, project.Id, project.MachineId, 8, order.TechnologistId, Token);
        Assert.Null(finished.Orders[0].Projects[0].Rename); Assert.True(File.Exists(finished.Orders[0].Projects[0].NativePath));
    }

    [Fact]
    public async Task StaleEditorCannotTouchFilesAndCalculatedProjectCannotBeRenamed()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var editor = f.Model.CreateProjectEditor(project.Id)!; editor.Thickness = "8";
        var book = await f.Store.ReadAsync(Token); var order = book.Orders[0];
        await f.Store.WriteOrderAsync(order.Id, order.Version, w => WorkflowChanges.UpdateOrder(w, order.Id, order.Version,
            order.ManagerId, order.TechnologistId, "123", order.TechnologistId, DateTimeOffset.UtcNow), Token);
        await editor.SaveCommand.ExecuteAsync(null); Assert.False(editor.WasSaved); Assert.Contains("изменились", editor.Error);
        Assert.Equal("8", editor.Thickness); Assert.True(File.Exists(project.NativePath));
        await f.Model.ReloadCommand.ExecuteAsync(null); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        Assert.Null(f.Model.CreateProjectEditor(project.Id));
    }

    [Fact]
    public async Task TrashPreservesEveryFileAndHistoryAndDoesNotSilentlyChooseAnAlternative()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var a = await f.AddAsync(); await ProjectFixture.WriteAsync(a); await f.AcceptAsync();
        var b = await f.AddAsync("Плазма", redo: true); await ProjectFixture.WriteAsync(b, 20000); await f.AcceptAsync();
        var before = await f.Store.ReadAsync(Token); var hashes = Hashes(before.ProjectRoot!);
        f.Model.ResultText = "Ручной текст письма\n";
        await f.Model.SetProjectTrashAsync(a.Id, true); Assert.Null(f.Model.Error);
        var order = (await f.Store.ReadAsync(Token)).Orders[0]; Assert.Equal(0, order.CuttingTotal);
        Assert.Equal(1, order.PendingProjectCount); Assert.Equal(a.Id, order.Items[0].CurrentProjectId);
        Assert.Equal(2, order.Specifications.Length); Assert.Single(f.Model.Drafts); Assert.True(f.Model.ResultIsStale);
        Assert.Equal("Ручной текст письма\n", f.Model.ResultText); Assert.Equal(hashes, Hashes(before.ProjectRoot!));
        var review = await f.ReviewAsync(); Assert.DoesNotContain(review.Candidates, c => c.Project.Id == a.Id);
        f.Model.SelectedProject = f.Model.Projects.Single(p => p.Project.Id == b.Id);
        await f.Model.SelectProjectForTotalCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        await f.Model.SetProjectTrashAsync(a.Id, false); Assert.Null(f.Model.Error);
        order = (await f.Store.ReadAsync(Token)).Orders[0]; Assert.Equal(b.Id, order.Items[0].CurrentProjectId); Assert.Equal(2105, order.CuttingTotal);
        Assert.Equal(hashes, Hashes(before.ProjectRoot!)); Assert.Equal(2, f.Model.Drafts.Count);
    }

    [Fact]
    public async Task TrashAndRestoreDoNotNeedThePhysicalFolder()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var p = await f.AddAsync();
        var source = Path.GetDirectoryName(p.NativePath)!; var moved = source + "-перемещено-вручную"; Directory.Move(source, moved);
        await f.Model.SetProjectTrashAsync(p.Id, true); Assert.Null(f.Model.Error); Assert.Empty(f.Model.Projects);
        await f.Model.SetProjectTrashAsync(p.Id, false); Assert.Null(f.Model.Error); Assert.Single(f.Model.Projects);
        Assert.False(Directory.Exists(source)); Assert.True(Directory.Exists(moved)); Assert.Equal(p.NativePath, f.Model.Projects[0].Project.NativePath);
    }

    [Fact]
    public async Task InclusionChangesTotalsCompletenessAndReportSearchWithoutDeletingOrLosingManualText()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var ready = await f.AddAsync();
        await ProjectFixture.WriteAsync(ready); await f.AcceptAsync(); var waiting = await f.AddAsync("Плазма");
        f.Model.ResultText = "Ручной результат";
        await f.Model.SetProjectIncludedAsync(waiting.Id, false); Assert.Null(f.Model.Error);
        var order = (await f.Store.ReadAsync(Token)).Orders[0]; Assert.Equal(0, order.PendingProjectCount); Assert.Equal(1105, order.CuttingTotal);
        Assert.Equal(2, f.Model.Projects.Count); Assert.Contains(f.Model.Projects, p => p.State == "Исключён из состава заявки");
        Assert.True(f.Model.ResultIsStale); Assert.Equal("Ручной результат", f.Model.ResultText);
        Assert.Single((await f.ReviewAsync()).Candidates);
        f.Model.GenerateResultCommand.Execute(null); Assert.Null(f.Model.Error); Assert.NotEmpty(f.Model.ResultText);
        await f.Model.SetProjectIncludedAsync(ready.Id, false); order = (await f.Store.ReadAsync(Token)).Orders[0];
        Assert.Equal(0, order.CuttingTotal); Assert.Equal(0, order.PendingProjectCount); Assert.Single(order.Specifications);
        Assert.True(File.Exists(ready.NativePath)); Assert.True(File.Exists(waiting.NativePath));
        await f.Model.SetProjectIncludedAsync(ready.Id, true); order = (await f.Store.ReadAsync(Token)).Orders[0]; Assert.Equal(1105, order.CuttingTotal);
        await f.Model.SetProjectIncludedAsync(waiting.Id, true); Assert.Equal(1, (await f.Store.ReadAsync(Token)).Orders[0].PendingProjectCount);
    }

    [Fact]
    public async Task SchemaSevenUpgradeKeepsProjectsAndSelectedVariantsAndBacksUpOriginal()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); await f.AddAsync();
        var before = await f.Store.ReadAsync(Token); var path = Path.Combine(f.DirectoryPath, "schema7.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(before with { SchemaVersion = 7 }))!;
        foreach (var order in node["Orders"]!.AsArray())
        {
            foreach (var item in order!["Items"]!.AsArray()) item!.AsObject().Remove("IsIncluded");
            foreach (var project in order["Projects"]!.AsArray())
            { project!.AsObject().Remove("DeletedAt"); project.AsObject().Remove("DeletedBy"); project.AsObject().Remove("Rename"); }
        }
        var json = node.ToJsonString(); await File.WriteAllTextAsync(path, json, Token);
        var after = await new LocalWorkflowWorkspaceStore(path).ReadAsync(Token);
        Assert.Equal(11, after.SchemaVersion); Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Orders[0].Projects[0], after.Orders[0].Projects[0]); Assert.True(after.Orders[0].Items[0].IsIncluded);
        Assert.Equal(json, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(f.DirectoryPath, "schema7.json.*.bak")), Token));
    }

    private static string[] Hashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order().Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
}
