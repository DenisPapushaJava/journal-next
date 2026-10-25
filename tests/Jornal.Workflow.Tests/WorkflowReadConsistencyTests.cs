using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowReadConsistencyTests
{
    [Theory]
    [InlineData(WorkflowAccessOperation.Inspect)] [InlineData(WorkflowAccessOperation.Read)]
    [InlineData(WorkflowAccessOperation.ReadOrders)] [InlineData(WorkflowAccessOperation.ReadTrash)]
    public async Task ReadUsesFreshMetadataOnlyAndKeepsTheOriginalProjection(WorkflowAccessOperation operation)
    {
        var wire = new ProcessFixture(); var request = new WorkflowAccessRequest(1, operation, "test.mdb");
        var response = await new WorkflowAccessClient(process: wire).SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Same(wire.Response, response); Assert.Equal(2, wire.Requests.Count);
        Assert.Equal(new(1, WorkflowAccessOperation.ReadRevision, request.Path, response.DatabaseId), wire.Requests[1]);
    }
    [Theory]
    [InlineData("revision")] [InlineData("catalog")] [InlineData("identity")]
    public async Task ConcurrentCommitOrReplacementNeverReturnsAMixedProjection(string changed)
    {
        var wire = new ProcessFixture { Changed = changed };
        await Assert.ThrowsAsync<WorkflowConflictException>(() => new WorkflowAccessClient(process: wire).SendAsync(
            new(1, WorkflowAccessOperation.Read, "test.mdb"), TestContext.Current.CancellationToken));
        Assert.Equal(2, wire.Requests.Count);
    }
    [Theory]
    [InlineData(WorkflowAccessOperation.Create)] [InlineData(WorkflowAccessOperation.Apply)] [InlineData(WorkflowAccessOperation.Purge)]
    public async Task WriteIsSentOnceAndItsConfirmedProjectionIsKept(WorkflowAccessOperation operation)
    {
        var wire = new ProcessFixture();
        Assert.Same(wire.Response, await new WorkflowAccessClient(process: wire).SendAsync(
            new(1, operation, "test.mdb"), TestContext.Current.CancellationToken));
        Assert.Single(wire.Requests);
    }
    [Fact]
    public async Task CancellationAfterProjectionDoesNotStartAnotherProcess()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var wire = new ProcessFixture { Cancel = cancelled };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WorkflowAccessClient(process: wire).SendAsync(
            new(1, WorkflowAccessOperation.Read, "test.mdb"), cancelled.Token));
        Assert.Single(wire.Requests);
    }
    private sealed class ProcessFixture : IWorkflowAccessTransport
    {
        public WorkflowAccessResponse Response { get; } = new(1, Guid.NewGuid(), 2, 7, Orders: []);
        public List<WorkflowAccessRequest> Requests { get; } = [];
        public string? Changed { get; init; }
        public CancellationTokenSource? Cancel { get; init; }
        public Task<WorkflowAccessResponse> SendAsync(WorkflowAccessRequest request, CancellationToken token = default)
        {
            Requests.Add(request); Cancel?.Cancel();
            return Task.FromResult(request.Operation != WorkflowAccessOperation.ReadRevision ? Response : Changed switch
            {
                "revision" => Response with { Revision = 8 }, "catalog" => Response with { CatalogVersion = 3 },
                "identity" => Response with { DatabaseId = Guid.NewGuid() }, _ => Response
            });
        }
    }
}
