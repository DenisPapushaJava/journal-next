using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowServiceFilesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProjectServiceArtifactsSurviveRepeatedRenameAndReportAcceptance()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync(); var project = await f.AddAsync();
        var book = await f.Store.ReadAsync(Token); var names = Path.Combine(book.ProjectRoot!, ".workflow-names");
        AssertHiddenOnWindows(names); AssertVisible(project.NativePath!);
        var initialFolder = Path.GetDirectoryName(project.NativePath!)!;
        var lease = Path.Combine(Path.GetDirectoryName(initialFolder)!, ".workflow-edit-" + project.Id.ToString("N") + ".lock");
        var service = new WorkflowProjectMaintenance(f.Store);
        var order = book.Orders.Single();
        book = await service.RenameAsync(order.Id, order.Version, project.Id, project.MachineId, 8, order.TechnologistId, Token);
        var renamed = book.Orders.Single().Projects.Single();
        var receipt = Path.Combine(Path.GetDirectoryName(renamed.NativePath!)!, ".workflow-project");
        AssertHiddenOnWindows(receipt); AssertHiddenOnWindows(lease);
        Assert.Equal(project.Id.ToString("N"), await File.ReadAllTextAsync(receipt, Token));
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(names, File.GetAttributes(names) & ~FileAttributes.Hidden);
            File.SetAttributes(receipt, File.GetAttributes(receipt) & ~FileAttributes.Hidden);
            File.SetAttributes(lease, File.GetAttributes(lease) & ~FileAttributes.Hidden);
        }
        // Existing service artifacts get their flag repaired only when actually used.
        order = book.Orders.Single();
        book = await service.RenameAsync(order.Id, order.Version, project.Id, project.MachineId, 6, order.TechnologistId, Token);
        renamed = book.Orders.Single().Projects.Single(); var folder = Path.GetDirectoryName(renamed.NativePath!)!;
        AssertHiddenOnWindows(Path.Combine(folder, ".workflow-project")); AssertHiddenOnWindows(lease);
        AssertVisible(folder); AssertVisible(renamed.NativePath!); Assert.Equal(0, new FileInfo(renamed.NativePath!).Length);
        await f.Model.ReloadCommand.ExecuteAsync(null); await f.AddAsync("Плазма"); AssertHiddenOnWindows(names);

        var userFile = Path.Combine(folder, ".мои-заметки"); await File.WriteAllTextAsync(userFile, "Не менять", Token);
        var userAttributes = File.GetAttributes(userFile);
        await ProjectFixture.WriteAsync(renamed); await f.AcceptAsync();
        var archive = Path.Combine(folder, ".workflow-reports"); AssertHiddenOnWindows(archive);
        if (OperatingSystem.IsWindows()) File.SetAttributes(archive, File.GetAttributes(archive) & ~FileAttributes.Hidden);
        await ProjectFixture.WriteAsync(renamed, 20000); await f.AcceptAsync(); AssertHiddenOnWindows(archive);
        AssertVisible(ProjectFixture.ReportPath(renamed)); Assert.Equal(userAttributes, File.GetAttributes(userFile));
        Assert.Equal("Не менять", await File.ReadAllTextAsync(userFile, Token));
        f.Model.SelectedProject = f.Model.Projects.Single(p => p.Project.Id == renamed.Id);
        f.Model.SelectedDraft!.HasBending = true; await f.Model.SaveSpecificationCommand.ExecuteAsync(null); Assert.Null(f.Model.Error);
        AssertVisible(Path.Combine(folder, "ГИБКА"));
        Assert.True(File.Exists(renamed.NativePath)); Assert.Equal(2, Directory.GetFiles(names).Length);
    }

    [Fact]
    public async Task WindowsHiddenFlagPreservesAttributesAndDoesNotBreakExclusiveLockReopen()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Hidden attribute writes and Windows sharing semantics require native Windows."); return; }
        using var f = new ProjectFixture();
        var receipt = Path.Combine(f.DirectoryPath, ".workflow-project"); await File.WriteAllTextAsync(receipt, "Ownership", Token);
        try
        {
            File.SetAttributes(receipt, FileAttributes.ReadOnly | FileAttributes.Archive);
            WorkflowServiceFiles.TryHide(receipt);
            var attributes = File.GetAttributes(receipt);
            Assert.True(attributes.HasFlag(FileAttributes.Hidden)); Assert.True(attributes.HasFlag(FileAttributes.ReadOnly)); Assert.True(attributes.HasFlag(FileAttributes.Archive));
            WorkflowServiceFiles.TryHide(receipt); Assert.Equal(attributes, File.GetAttributes(receipt));
            Assert.Equal("Ownership", await File.ReadAllTextAsync(receipt, Token));
        }
        finally { File.SetAttributes(receipt, FileAttributes.Normal); }
        var lease = Path.Combine(f.DirectoryPath, ".workflow-edit-test.lock");
        using (var file = new FileStream(lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        { file.WriteByte(42); file.Flush(true); WorkflowServiceFiles.TryHide(lease); AssertHiddenOnWindows(lease); }
        using (var reopened = new FileStream(lease, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        { Assert.Equal(1, reopened.Length); Assert.Equal(42, reopened.ReadByte()); }
    }

    [Fact]
    public void CosmeticAttributeFailureDoesNotCreateOrFailAMissingFile()
    {
        using var f = new ProjectFixture(); var missing = Path.Combine(f.DirectoryPath, ".workflow-project");
        WorkflowServiceFiles.TryHide(missing); Assert.False(File.Exists(missing)); Assert.Empty(Directory.EnumerateFileSystemEntries(f.DirectoryPath));
    }

    private static void AssertHiddenOnWindows(string path)
    { Assert.True(Path.Exists(path)); if (OperatingSystem.IsWindows()) Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden), path); }
    private static void AssertVisible(string path) => Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden), path);
}
