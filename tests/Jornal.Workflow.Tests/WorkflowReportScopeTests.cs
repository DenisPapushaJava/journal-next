using Jornal.Workflow.Infrastructure;
using System.Text.Json;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowReportScopeTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("block-html/v1", "Спецификация.HTML", true)]
    [InlineData("block-html/v1", "report.htm", true)]
    [InlineData("block-html/v1", "Карта.pdf", false)]
    [InlineData("hans-pdf/v1", "Спецификация.PDF", true)]
    [InlineData("hans-pdf/v1", "other.html", false)]
    [InlineData("waterjet-awjc/v1", "AWJC.PDF", true)]
    [InlineData("waterjet-awjc/v1", "other.htm", false)]
    [InlineData(null, "document.pdf", true)]
    [InlineData(null, "program.cnc", false)]
    public void FormatsFollowProjectProfile(string? profile, string path, bool expected) =>
        Assert.Equal(expected, WorkflowReportFiles.IsReport(path, profile));

    [Fact]
    public async Task SharedFolderDoesNotReuseHtmlScanForPdfProject()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var block = await f.AddAsync(); await ProjectFixture.WriteAsync(block); await f.AcceptAsync();
        var hans = await f.AddAsync("HansLaser");
        var folder = Path.GetDirectoryName(block.NativePath!)!;
        var native = Path.Combine(folder, Path.GetFileName(hans.NativePath!));
        await File.WriteAllTextAsync(native, "", Token);
        await File.WriteAllTextAsync(Path.Combine(folder, "Спецификация.pdf"), "Unreadable PDF specification", Token);
        var before = await f.Store.ReadAsync(Token); var order = Assert.Single(before.Orders);
        await f.Store.WriteAsync(before.Version, book => book with
        {
            Orders = [order with { Projects = order.Projects.Select(p => p.Id == hans.Id ? p with { NativePath = native } : p).ToArray() }]
        }, Token);
        await f.Model.ReloadCommand.ExecuteAsync(null);
        var review = await f.ReviewAsync();
        var html = Assert.Single(review.Candidates, c => c.Project.Id == block.Id);
        Assert.Equal("Без изменений", html.Status); Assert.False(html.NeedsAttention);
        var pdf = Assert.Single(review.Candidates, c => c.Project.Id == hans.Id);
        Assert.Empty(pdf.Choices); Assert.Contains("неполный", pdf.Status);
    }

    [Fact]
    public async Task UnrelatedPdfDoesNotInvalidateAcceptedBlockReportOrBlockHandoff()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        f.Model.Blcb = "000837"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        var pdf = Path.Combine(Path.GetDirectoryName(project.NativePath!)!, "Карта.pdf");
        await File.WriteAllTextAsync(pdf, "Not a specification; must not be opened by the HTML scan.", Token);
        using var locked = File.Open(pdf, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var before = await f.Store.ReadAsync(Token);
        var review = await f.ReviewAsync(); var candidate = Assert.Single(review.Candidates);
        Assert.Equal("Без изменений", candidate.Status);
        Assert.False(candidate.NeedsAttention);
        Assert.NotNull(candidate.SelectedFile);
        Assert.Equal("Готово 1 из 1", f.Model.CompletenessCaption);
        Assert.True(f.Model.CanSendToWork, f.Model.SendToWorkHint);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await f.Store.ReadAsync(Token)));
    }

    [Fact]
    public async Task ChangedHtmlStillNeedsAcceptanceWhenMapIsPresent()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        f.Model.Blcb = "000837"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(project.NativePath!)!, "Карта.pdf"), "map", Token);
        await ProjectFixture.WriteAsync(project, 20000);
        var review = await f.ReviewAsync(); var candidate = Assert.Single(review.Candidates);
        Assert.NotNull(candidate.SelectedFile);
        Assert.True(candidate.NeedsAttention);
        Assert.False(f.Model.CanSendToWork);
        Assert.Equal("Готово 0 из 1", f.Model.CompletenessCaption);
    }

    [Fact]
    public async Task UnreadableHtmlStillMakesTheScanIncomplete()
    {
        using var f = new ProjectFixture(); await f.InitializeAsync();
        var project = await f.AddAsync(); await ProjectFixture.WriteAsync(project); await f.AcceptAsync();
        f.Model.Blcb = "000837"; await f.Model.SaveHeaderCommand.ExecuteAsync(null);
        var html = Path.Combine(Path.GetDirectoryName(project.NativePath!)!, "Исправленный.html");
        await File.WriteAllTextAsync(html, ProjectFixture.Html(project, 20000), Token);
        using var locked = File.Open(html, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var candidate = Assert.Single((await f.ReviewAsync()).Candidates);
        Assert.Null(candidate.SelectedFile);
        Assert.True(candidate.NeedsAttention);
        Assert.Contains("неполный", candidate.Status);
        Assert.False(f.Model.CanSendToWork);
        candidate.SelectedFile = Assert.Single(candidate.Choices);
        f.Model.ReportReviewClosed();
        Assert.Equal("Без изменений", candidate.Status);
        Assert.False(candidate.NeedsAttention);
        Assert.True(f.Model.CanSendToWork, f.Model.SendToWorkHint);
    }
}
