using Jornal.Workflow.Desktop;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowUpdateBoundaryTests
{
    [Fact]
    public async Task LegacyInstallationIsRejectedBeforeAnyFeedRequest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "workflow-installed-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var updater = new WorkflowAppUpdater(managerFactory: _ => new UpdateManager(new SimpleWebSource("http://127.0.0.1:1"),
                locator: new TestVelopackLocator("DenisPapusha.Journal", "2026.1.0-alpha.1", directory)));
            await Assert.ThrowsAsync<InvalidDataException>(() => updater.CheckAsync(true, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealHttpFeedAcceptsOwnPackageAndRejectsForeignPackage(bool foreign)
    {
        var url = Environment.GetEnvironmentVariable("WORKFLOW_SIMULATION_URL");
        if (url is null) { Assert.Skip("Start the local Postman workflow-updates simulation to exercise real HTTP."); return; }
        var directory = Path.Combine(Path.GetTempPath(), "workflow-http-boundary-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var source = url.TrimEnd('/') + (foreign ? "/foreign" : "/own");
            var updater = new WorkflowAppUpdater(managerFactory: _ => new UpdateManager(new SimpleWebSource(source),
                new UpdateOptions { ExplicitChannel = "workflow-win-x64", AllowVersionDowngrade = false },
                new TestVelopackLocator(WorkflowIdentity.PackageId, "2026.1.0-alpha.1", directory)));
            if (foreign)
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => updater.CheckAsync(true, TestContext.Current.CancellationToken));
                Assert.Empty(Directory.GetFiles(directory, "*.nupkg"));
            }
            else
            {
                var candidate = await updater.CheckAsync(true, TestContext.Current.CancellationToken); Assert.NotNull(candidate);
                await updater.DownloadAsync(candidate, new Progress<int>(), TestContext.Current.CancellationToken);
                Assert.Single(Directory.GetFiles(directory, "DenisPapusha.JournalWorkflow*.nupkg"));
                Assert.Throws<InvalidOperationException>(() => updater.ScheduleRestart(candidate with { Version = "other" }));
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
