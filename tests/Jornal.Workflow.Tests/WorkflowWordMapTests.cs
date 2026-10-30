using System.Security.Cryptography;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowWordMapTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Project = "4 mm 2026-10-29-121314--HANSLASER.Dsp";

    private static WordMapCell[] Table(string project = Project, string comment = "", int offset = 0) =>
    [new(offset, 1, "Заказ\r\a"), new(offset + 1, 1, project + "\r\a"), new(offset + 2, 1, "29.10.2026"),
        new(offset + 3, 2, "Файл УП"), new(offset + 4, 2, "Лист1"), new(offset + 5, 3, "Комментарий"), new(offset + 6, 3, comment + "\r\a")];

    [Fact]
    public void FindsCommentsByLabelsAndSameRowWithoutFixedColumnsAndAllowsEmptyOrder()
    {
        Assert.Equal([6, 16], WorkflowWordMapRules.Comments([Table(), Table("", offset: 10)], Project)!);
        Assert.Equal([6], WorkflowWordMapRules.Comments([Table(Project.ToUpperInvariant().Replace(" ", "  "))], Project)!);
        Assert.Null(WorkflowWordMapRules.Comments([Table("другой.Dsp")], Project));
        Assert.Null(WorkflowWordMapRules.Comments([[new(0, 1, "Счёт"), new(1, 2, "Заказчик")]], Project));
    }

    [Fact]
    public void MixedProjectsAndBrokenRowsAreRejected()
    {
        Assert.Throws<WorkflowValidationException>(() => WorkflowWordMapRules.Comments([Table(), Table("чужой.Dsp", offset: 10)], Project));
        var table = Table(); table[^1] = table[^1] with { Row = 4 };
        Assert.Throws<WorkflowValidationException>(() => WorkflowWordMapRules.Comments([table], Project));
    }

    [Fact]
    public void FindsEveryCardInOneTableAndRejectsMixedOrIncompleteBlocks()
    {
        var second = Table(offset: 10).Select(c => c with { Row = c.Row + 3 }).ToArray();
        Assert.Equal([6, 16], WorkflowWordMapRules.Comments([[.. Table(), .. second]], Project)!);
        second[1] = second[1] with { Text = "чужой.Dsp" };
        Assert.Throws<WorkflowValidationException>(() => WorkflowWordMapRules.Comments([[.. Table(), .. second]], Project));
        second[1] = second[1] with { Text = Project };
        Assert.Throws<WorkflowValidationException>(() => WorkflowWordMapRules.Comments([[.. Table(), .. second[..^2]]], Project));
        Assert.Throws<WorkflowValidationException>(() => WorkflowWordMapRules.Comments([[.. Table(), .. second[3..]]], Project));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Пластины для цеха под Ханс")]
    [InlineData("(4 мм) 1029-121314, Юрченко, ЗПТО, БЛЦБ-009337")]
    [InlineData("Любая ручная подпись\r\a")]
    public void OnlyCommentValueIsSelectedRegardlessOfItsTextOrNotesElsewhere(string caption)
    {
        WordMapCell[] cells = [.. Table(comment: caption), new(7, 4, "Пометка технолога вне комментария")];
        Assert.Equal([6], WorkflowWordMapRules.Comments([cells], Project)!);
    }

    private sealed class Editor(Func<byte[], WordMapEdit?> edit) : IWorkflowWordMapEditor
    {
        internal int Calls; internal List<string> Projects = [];
        public Task<WordMapEdit?> PrepareAsync(byte[] source, string projectFileName, string caption, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++; Projects.Add(projectFileName);
            Assert.Contains("БЛЦБ-009337", caption);
            return Task.FromResult(edit(source));
        }
    }

    [Fact]
    public async Task MultipleDocsAreIndependentBusyFailureDoesNotPreventOthersAndBackupsKeepExactBytes()
    {
        using var f = new MapSigningFixture("HansLaser");
        var a = Path.Combine(f.Root, "1.DOC"); var b = Path.Combine(f.Root, "2.doc"); var busy = Path.Combine(f.Root, "занята.DOC");
        await File.WriteAllTextAsync(a, "one", Token); await File.WriteAllTextAsync(b, "two", Token);
        await File.WriteAllTextAsync(busy, "busy", Token); await File.WriteAllTextAsync(Path.Combine(f.Root, "~$owner.doc"), "owner", Token);
        var editor = new Editor(bytes => new(bytes.Concat(new byte[] { 42 }).ToArray(), 1, 1));
        MapSigningResult[] result;
        using (File.Open(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await WorkflowMapSigning.SignAsync(f.Order, f.Book, null, _ => Task.CompletedTask, Token, editor);
        Assert.Equal(2, editor.Calls); Assert.Equal(3, result.Length);
        foreach (var signed in result.Where(r => r.Status == MapSigningStatus.Signed))
        {
            var original = await File.ReadAllBytesAsync(signed.BackupPath!, Token);
            Assert.Equal(original.Concat(new byte[] { 42 }), await File.ReadAllBytesAsync(signed.FilePath!, Token));
            Assert.Equal(".doc", Path.GetExtension(signed.BackupPath!).ToLowerInvariant());
        }
        Assert.Equal(2, result.Count(r => r.Status == MapSigningStatus.Signed));
        Assert.Equal(busy, Assert.Single(result, r => r.Status == MapSigningStatus.Failed).FilePath);
    }

    [Fact]
    public async Task NoOpLeavesMtimeAndDoesNotCreateBackup()
    {
        using var f = new MapSigningFixture("HansLaser"); var path = Path.Combine(f.Root, "Карта.doc");
        await File.WriteAllTextAsync(path, "same", Token); var time = File.GetLastWriteTimeUtc(path);
        var editor = new Editor(_ => new(null, 1, 0));
        var results = await WorkflowMapSigning.SignAsync(f.Order, f.Book, null, _ => Task.CompletedTask, Token, editor);
        Assert.Equal(MapSigningStatus.Unchanged, Assert.Single(results).Status);
        Assert.Equal(time, File.GetLastWriteTimeUtc(path)); Assert.Equal("same", await File.ReadAllTextAsync(path, Token));
        Assert.False(Directory.Exists(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder)));
    }

    [Fact]
    public async Task MissingOrHungWordIsReportedOnceAndNotRetriedForEveryDoc()
    {
        using var f = new MapSigningFixture("HansLaser");
        for (var i = 0; i < 4; i++) await File.WriteAllTextAsync(Path.Combine(f.Root, i + ".doc"), "original", Token);
        var editor = new Editor(_ => throw new WordMapUnavailableException("Microsoft Word не найден. DOC-карты не подписаны."));
        var results = await WorkflowMapSigning.SignAsync(f.Order, f.Book, null, _ => Task.CompletedTask, Token, editor);
        Assert.Equal(1, editor.Calls); Assert.Contains("Word не найден", Assert.Single(results).Message);
        Assert.False(Directory.Exists(Path.Combine(f.Root, WorkflowMapSigning.BackupFolder)));
        foreach (var path in Directory.GetFiles(f.Root, "*.doc")) Assert.Equal("original", await File.ReadAllTextAsync(path, Token));
    }

    [Fact]
    public async Task ConcurrentSourceOrOrderChangePreventsReplacingDoc()
    {
        using var f = new MapSigningFixture("HansLaser"); var path = Path.Combine(f.Root, "Карта.doc");
        await File.WriteAllTextAsync(path, "original", Token);
        var editor = new Editor(_ => { File.WriteAllText(path, "external"); return new([42], 1, 1); });
        var results = await WorkflowMapSigning.SignAsync(f.Order, f.Book, null, _ => Task.CompletedTask, Token, editor);
        Assert.Equal(MapSigningStatus.Failed, Assert.Single(results).Status); Assert.Equal("external", await File.ReadAllTextAsync(path, Token));
        editor = new Editor(_ => new([42], 1, 1));
        results = await WorkflowMapSigning.SignAsync(f.Order, f.Book, null, _ => throw new WorkflowValidationException("Заявка изменилась"), Token, editor);
        Assert.Equal(MapSigningStatus.Failed, Assert.Single(results).Status); Assert.Equal("external", await File.ReadAllTextAsync(path, Token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task NoBlcbNeverInvokesWordAndNeverTouchesDocs(string? blcb)
    {
        using var f = new MapSigningFixture("HansLaser"); var path = Path.Combine(f.Root, "Карта.doc");
        f.Book = f.Book with { Orders = [f.Order with { Blcb = blcb }] };
        await File.WriteAllTextAsync(path, "original", Token); var editor = new Editor(_ => new([42], 1, 1));
        var results = await WorkflowMapSigning.SignAsync(f.Order, f.Book, null, _ => Task.CompletedTask, Token, editor);
        Assert.Contains("БЛЦБ", Assert.Single(results).Message); Assert.Equal(0, editor.Calls);
        Assert.Equal("original", await File.ReadAllTextAsync(path, Token));
    }

    [Fact]
    public async Task NativeWordProbeWorksOnlyOnCopiesAndIsIdempotent()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("WORKFLOW_WORD_MAP_FIXTURE") is not { } source ||
            Environment.GetEnvironmentVariable("WORKFLOW_WORD_MAP_APP") is not { } executable ||
            Environment.GetEnvironmentVariable("WORKFLOW_WORD_MAP_PROJECT") is not { } project)
        { Assert.Skip("Requires Windows, installed Word, and explicit private DOC fixture / application / project name."); return; }
        var bytes = await File.ReadAllBytesAsync(source, Token); var hash = SHA256.HashData(bytes);
        var editor = new WorkflowWordMapProcess(executable);
        var caption = "(4 мм)  1029-121314,  Юрченко,  (ЗПТО),  БЛЦБ-009337";
        var result = await editor.PrepareAsync(bytes, project, caption, Token);
        Assert.NotNull(result); Assert.NotNull(result.Bytes);
        var again = await editor.PrepareAsync(result.Bytes, project, caption, Token);
        Assert.NotNull(again); Assert.Null(again.Bytes);
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(source, Token)));
    }
}
