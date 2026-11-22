using System.Net;
using System.Text;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowOfficePreviewTests
{
    private const string Address = "http://10.0.0.2:60913/";
    private static readonly string PreviewKey = new('A', 64);

    [Fact]
    public async Task PreviewReadsTheExistingAdminListWithOneGet()
    {
        var calls = 0;
        using var http = new HttpClient(new PreviewHandler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new Uri(Address + "api/v1/orders"), request.RequestUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(PreviewKey, request.Headers.Authorization?.Parameter);
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"totalCount":21,"hasMore":true,"orders":[{"number":"Тест-01","customer":"Заказчик"}]}
                    """, Encoding.UTF8, "application/json")
            };
        }));
        var preview = new WorkflowOfficePreviewClient(http);

        var result = await preview.ReadAsync(Address, PreviewKey, TestContext.Current.CancellationToken);

        Assert.Equal(1, calls);
        Assert.Equal(21, result.TotalCount);
        Assert.Equal(new WorkflowOfficePreviewRow("Тест-01", "Заказчик"), Assert.Single(result.Orders));
    }

    [Theory]
    [InlineData("http://8.8.8.8:60913/")]
    [InlineData("http://localhost:60913/")]
    [InlineData("http://10.0.0.2:60913/api/v1/")]
    [InlineData("http://name@10.0.0.2:60913/")]
    public async Task InvalidAddressIsRejectedBeforeFetchingOrders(string address)
    {
        using var http = new HttpClient(new PreviewHandler(_ => throw new InvalidOperationException("Unexpected request")));
        var preview = new WorkflowOfficePreviewClient(http);

        await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            preview.ReadAsync(address, PreviewKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectedAdministratorKeyHasAnActionableMessage()
    {
        using var http = new HttpClient(new PreviewHandler(_ => new(HttpStatusCode.Unauthorized)));
        var preview = new WorkflowOfficePreviewClient(http);

        var error = await Assert.ThrowsAsync<WorkflowValidationException>(() =>
            preview.ReadAsync(Address, PreviewKey, TestContext.Current.CancellationToken));

        Assert.Contains("Скопируйте его заново", error.Message);
        Assert.DoesNotContain(PreviewKey, error.Message);
    }

    private sealed class PreviewHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
