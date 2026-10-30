using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowMapSigningReviewTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Editor : IWorkflowWordMapEditor
    {
        internal int Calls;
        public Task<WordMapEdit?> PrepareAsync(byte[] bytes, string project, string caption, CancellationToken token)
        { Calls++; return Task.FromResult<WordMapEdit?>(new([42], 1, 1)); }
    }

    private sealed class FaultingCommit(string failure, int failAt) : WorkflowMapCommit
    {
        private int _calls;
        internal override void Replace(string stage, string path, string backup)
        {
            _calls++;
            if (_calls == failAt && failure == "before") throw new IOException("private replace error");
            if (_calls == failAt && failure == "rename")
            {
                File.Move(path, path + ".before-external-rename");
                File.WriteAllText(path, "external replacement");
            }
            base.Replace(stage, path, backup);
            if (_calls == failAt && failure == "after") throw new IOException("private error after actual replacement");
        }
        internal override Task<(long Length, string Hash)> FingerprintAsync(string path) => _calls == failAt && failure == "verify" ?
            throw new IOException("private post-commit read error") : base.FingerprintAsync(path);
    }

    [Theory]
    [InlineData("Лазер", "verify", false)] [InlineData("ГАР", "verify", false)]
    [InlineData("HansLaser", "verify", false)] [InlineData("HansLaser", "before", false)]
    [InlineData("HansLaser", "after", false)] [InlineData("HansLaser", "rename", false)]
    [InlineData("HansLaser", "verify", true)]
    public async Task UnconfirmedCommitPreservesPathsStopsTheBatchAndNeverReportsANormalRetry(string machine, string failure, bool partial)
    {
        using var f = new MapSigningFixture(machine);
        string Create(string name)
        {
            if (machine == "ГАР") return WaterjetMapFixture.Create(f, name + ".pdf");
            if (machine == "Лазер") return f.Create(name + ".pdf");
            var path = Path.Combine(f.Root, name + ".doc"); File.WriteAllText(path, "original"); return path;
        }
        var first = Create("1"); var second = Create("2"); var last = Create("3");
        var affected = partial ? second : first; var original = await File.ReadAllBytesAsync(affected, Token);
        var untouched = await File.ReadAllBytesAsync(last, Token); var editor = new Editor();
        var results = await WorkflowMapSigning.SignAsync(f.Order, f.Book, null, _ => Task.CompletedTask, Token, editor,
            new FaultingCommit(failure, partial ? 2 : 1));
        Assert.Equal(partial ? 2 : 1, results.Length);
        var uncertain = Assert.Single(results, r => r.Status == MapSigningStatus.NeedsVerification);
        Assert.True(uncertain.NeedsAttention); Assert.True(uncertain.MayHaveChanged);
        Assert.Equal(affected, uncertain.FilePath); Assert.NotNull(uncertain.BackupPath);
        Assert.Contains(uncertain.BackupPath, uncertain.Details); Assert.Contains("остановлено", uncertain.Message);
        Assert.DoesNotContain("повторите", uncertain.Message); Assert.DoesNotContain("private", uncertain.Details);
        if (failure == "before") Assert.Equal(original, await File.ReadAllBytesAsync(affected, Token));
        else
        {
            Assert.NotEqual(original, await File.ReadAllBytesAsync(affected, Token));
            if (failure == "rename") Assert.Equal("external replacement", await File.ReadAllTextAsync(uncertain.BackupPath, Token));
            else Assert.Equal(original, await File.ReadAllBytesAsync(uncertain.BackupPath, Token));
        }
        Assert.Equal(untouched, await File.ReadAllBytesAsync(last, Token));
        if (machine == "HansLaser") Assert.Equal(partial ? 2 : 1, editor.Calls);
        if (partial) Assert.Equal(MapSigningStatus.Signed, results[0].Status);
        var service = Path.Combine(f.Root, WorkflowMapSigning.BackupFolder);
        Assert.Empty(Directory.GetFiles(service, "*.tmp"));
        using var unlocked = File.Open(Path.Combine(service, "signing.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task AChangedSourceFileDoesNotStopIndependentFiles()
    {
        using var f = new MapSigningFixture("HansLaser"); var a = Path.Combine(f.Root, "1.doc"); var b = Path.Combine(f.Root, "2.doc");
        await File.WriteAllTextAsync(a, "original", Token); await File.WriteAllTextAsync(b, "original", Token);
        var checks = 0; var editor = new Editor();
        var results = await WorkflowMapSigning.SignAsync(f.Order, f.Book, null, _ =>
        {
            if (++checks == 1) File.WriteAllText(a, "external edit");
            return Task.CompletedTask;
        }, Token, editor);
        Assert.Equal(2, editor.Calls); Assert.Equal(2, results.Length);
        Assert.Equal(MapSigningStatus.Failed, results[0].Status); Assert.Equal(MapSigningStatus.Signed, results[1].Status);
        Assert.Equal("external edit", await File.ReadAllTextAsync(a, Token));
        Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(b, Token));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public async Task ValidationFailureStopsRemainingFilesAndProjectsAndKeepsCompletedResults(bool partial, bool unavailable)
    {
        using var f = new MapSigningFixture("HansLaser");
        for (var i = 1; i <= 3; i++) await File.WriteAllTextAsync(Path.Combine(f.Root, i + ".doc"), "original", Token);
        var otherRoot = Path.Combine(f.Root, "other"); Directory.CreateDirectory(otherRoot);
        var other = f.Project with { Id = Guid.NewGuid(), ItemId = Guid.NewGuid(), NativePath = Path.Combine(otherRoot, "other.Dsp"), Name = "other" };
        await File.WriteAllTextAsync(other.NativePath!, "project", Token);
        await File.WriteAllTextAsync(Path.Combine(otherRoot, "other.doc"), "original", Token);
        f.Book = f.Book with { Orders = [f.Order with {
            Projects = [f.Project, other],
            Items = [.. f.Order.Items, new(other.ItemId, 2, other.Id)],
            Specifications = [.. f.Order.Specifications, f.Order.Specifications[0] with { Id = other.Id }]
        }] };
        var editor = new Editor(); var checks = 0;
        var results = await WorkflowMapSigning.SignAsync(f.Order, f.Book, null, _ =>
        {
            if (++checks == (partial ? 2 : 1))
                throw unavailable ? new IOException("private database failure") : new WorkflowValidationException("Заявка изменена другим пользователем.");
            return Task.CompletedTask;
        }, Token, editor);
        Assert.Equal(partial ? 2 : 1, editor.Calls); Assert.Equal(editor.Calls, checks);
        Assert.Equal(partial ? 2 : 1, results.Length);
        var failed = Assert.Single(results, r => r.Status == MapSigningStatus.Failed);
        Assert.Contains("остановлено", failed.Message); Assert.Contains("F5", failed.Message);
        if (!unavailable) Assert.Contains("изменена", failed.Message);
        Assert.DoesNotContain("private", failed.Message);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(f.Root, "3.doc"), Token));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(otherRoot, "other.doc"), Token));
        if (partial)
        {
            var signed = Assert.Single(results, r => r.Status == MapSigningStatus.Signed);
            Assert.Equal(new byte[] { 42 }, await File.ReadAllBytesAsync(signed.FilePath!, Token));
            Assert.Equal("original", await File.ReadAllTextAsync(signed.BackupPath!, Token));
        }
    }
}
