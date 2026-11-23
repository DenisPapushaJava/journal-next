using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Desktop;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class WorkflowOfficeSetupTests
{
    private const string NetworkId = "0123456789abcdef";
    private static readonly Uri Server = new("http://10.0.0.2:60913/");
    private static readonly ZeroTierBinding Binding = new(IPAddress.Parse("10.0.0.3"), "ZeroTier One", 24);
    private static readonly WorkflowOfficeRoute Route = new(Binding.Address, Binding.InterfaceAlias);
    private static readonly WorkflowOfficeRoute ObservedRoute = Route with { InterfaceId = "test-adapter" };
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PreviouslyVerifiedAdapterSkipsNetworkSetup()
    {
        var setup = NewSetup(WorkflowOfficeHealth.Reachable, ObservedRoute);
        var result = await setup.PrepareAsync(Server, null, true, _ => throw new InvalidOperationException(), Token,
            expectedRoute: new(null, null, ObservedRoute.InterfaceId));
        Assert.Equal(WorkflowOfficeSetupStep.Ready, result.Step);
        Assert.Equal(ObservedRoute, result.Route);
    }

    [Fact]
    public async Task InvalidNetworkIdDoesNotJoinAnyNetwork()
    {
        var result = await NewSetup(WorkflowOfficeHealth.RouteMissing).PrepareAsync(Server, "invalid", true, _ => { }, Token);
        Assert.Equal(WorkflowOfficeSetupStep.NetworkId, result.Step);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconfirmedConnectionDoesNotProbeOrAdvanceToAuthorization(bool windows)
    {
        var setup = new WorkflowOfficeSetup((_, _, _) => throw new InvalidOperationException("No unconfirmed probe"),
            (_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(), TimeSpan.Zero);
        var result = await setup.PrepareAsync(Server, null, windows, _ => { }, Token);
        Assert.Equal(WorkflowOfficeSetupStep.NetworkId, result.Step);
        Assert.Null(result.Route);
    }

    [Theory]
    [InlineData("office-network", true)]
    [InlineData("other", false)]
    public async Task ConfirmedAdapterMustMatchTheHealthProbe(string name, bool matches)
    {
        var observed = new WorkflowOfficeRoute(IPAddress.Parse("10.0.0.3"), name, "observed");
        var result = await NewSetup(WorkflowOfficeHealth.Reachable, observed).PrepareAsync(Server, null, false, _ => { }, Token,
            expectedRoute: new(null, "office-network"));
        Assert.Equal(matches ? WorkflowOfficeSetupStep.Ready : WorkflowOfficeSetupStep.NetworkId, result.Step);
        Assert.Equal(matches ? observed : null, result.Route);
    }

    [Theory]
    [InlineData("0123456789abcdef", "ztm2vo5ktg", "EEDEFC12B847")]
    [InlineData("0123456789abcd52", "ztm2vo5kw3", "32DEFC12B847")]
    public void LinuxAdapterMatchesTheZeroTierNameAndMac(string networkId, string interfaceName, string mac)
    {
        Assert.True(WorkflowOfficeLinuxNetwork.TryGetAdapter(networkId, "13579bdf02", out var name, out var address));
        Assert.Equal(interfaceName, name);
        Assert.Equal(mac, Convert.ToHexString(address));
    }

    [Theory]
    [InlineData("0123456789abcdef=zt-office\r\n0123456789abcdee=zt-other", "zt-office")]
    [InlineData("0123456789abcdef=zt-previous\n0123456789ABCDEF=office-tap", "office-tap")]
    [InlineData("0123456789abcdee=zt-other", null)]
    [InlineData("0123456789abcdef=\n0123456789abcdef=a_name_too_long_for_linux", null)]
    public void LinuxLegacyAdapterNameUsesTheExactNetworkId(string map, string? expected) =>
        Assert.Equal(expected, WorkflowOfficeLinuxNetwork.FindMappedName(map, NetworkId));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinuxNetworkIdMustResolveLocallyBeforeContactingTheServer(bool joined)
    {
        var probes = 0;
        var setup = new WorkflowOfficeSetup((_, expected, _) =>
        {
            probes++;
            Assert.Equal(ObservedRoute, expected);
            return Task.FromResult(new WorkflowOfficeProbe(WorkflowOfficeHealth.Reachable, ObservedRoute));
        }, (_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(), TimeSpan.Zero, findLinuxRoute: (id, _) =>
            {
                Assert.Equal(NetworkId, id);
                return Task.FromResult(joined ? ObservedRoute : null);
            });
        var result = await setup.PrepareAsync(Server, NetworkId, false, _ => { }, Token);
        Assert.Equal(joined ? WorkflowOfficeSetupStep.Ready : WorkflowOfficeSetupStep.NetworkId, result.Step);
        Assert.Equal(joined ? 1 : 0, probes);
    }

    [Fact]
    public async Task ApprovalDoesNotAdvanceUntilTheExpectedRouteAndHealthAreReady()
    {
        var published = new List<WorkflowOfficeSetupState>();
        var networks = new Queue<ZeroTierNetwork>([new("NOT_JOINED"), new("ACCESS_DENIED"), new("ADDRESS_PENDING"), new("OK", Binding), new("OK", Binding)]);
        var checkedSources = new List<WorkflowOfficeRoute?>();
        var calls = 0;
        var setup = new WorkflowOfficeSetup((server, source, _) =>
        {
            Assert.Equal(Server, server);
            checkedSources.Add(source);
            return Task.FromResult(checkedSources.Count == 2 ? new WorkflowOfficeProbe(WorkflowOfficeHealth.Reachable, Route) : new(WorkflowOfficeHealth.RouteMissing));
        }, (id, _) =>
        {
            Assert.Equal(NetworkId, id);
            return Task.FromResult(networks.Dequeue());
        }, (id, _) =>
        {
            Assert.Equal(NetworkId, id);
            calls++;
            return Task.FromResult(true);
        }, _ => Task.FromResult("0123456789"), TimeSpan.Zero, 5);

        var result = await setup.PrepareAsync(Server, NetworkId, true, published.Add, Token);

        Assert.Equal(WorkflowOfficeSetupStep.Ready, result.Step);
        Assert.Equal(1, calls);
        Assert.Equal(Route, result.Route);
        Assert.Equal(new[] { Route, Route }, checkedSources);
        var waitingIndex = published.FindIndex(s => s.Step == WorkflowOfficeSetupStep.WaitingApproval);
        Assert.Equal("0123456789", published[waitingIndex].NodeId);
        Assert.Contains(published.Skip(waitingIndex + 1), s => s.Step == WorkflowOfficeSetupStep.WaitingRoute);
    }

    [Fact]
    public async Task ClosingWhileWaitingStopsNetworkChecks()
    {
        using var closing = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var reads = 0;
        var setup = new WorkflowOfficeSetup((_, _, _) => Task.FromResult(new WorkflowOfficeProbe(WorkflowOfficeHealth.RouteMissing)), (_, _) =>
        {
            reads++;
            return Task.FromResult(new ZeroTierNetwork("ACCESS_DENIED"));
        }, (_, _) => Task.FromResult(true), _ => Task.FromResult("0123456789"), TimeSpan.FromSeconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.PrepareAsync(Server, NetworkId, true,
            state => { if (state.Step == WorkflowOfficeSetupStep.WaitingApproval) closing.Cancel(); }, closing.Token));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task UnavailableServerIsReportedAfterRouteIsReady()
    {
        var healthChecks = 0;
        var setup = new WorkflowOfficeSetup((_, source, _) =>
        {
            healthChecks++;
            Assert.Equal(Route, source);
            return Task.FromResult(new WorkflowOfficeProbe(WorkflowOfficeHealth.Unavailable));
        }, (_, _) => Task.FromResult(new ZeroTierNetwork("OK", Binding)), (_, _) => Task.FromResult(true),
            _ => throw new InvalidOperationException(), TimeSpan.Zero);
        var result = await setup.PrepareAsync(Server, NetworkId, true, _ => { }, Token);
        Assert.Equal(3, healthChecks);
        Assert.Equal(WorkflowOfficeSetupStep.Error, result.Step);
        Assert.Contains("Сервер не отвечает", result.Message);
    }

    [Fact]
    public async Task KnownNetworkRejectsHealthThroughAnotherRoute()
    {
        var other = new WorkflowOfficeRoute(IPAddress.Parse("10.0.0.4"), "Другая сеть");
        var checks = 0;
        var setup = new WorkflowOfficeSetup((_, expected, _) =>
        {
            Assert.Equal(Route, expected);
            checks++;
            return Task.FromResult(new WorkflowOfficeProbe(WorkflowOfficeHealth.Reachable, other));
        }, (_, _) => Task.FromResult(new ZeroTierNetwork("OK", Binding)), (_, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException(), TimeSpan.Zero, 2);
        var result = await setup.PrepareAsync(Server, NetworkId, true, _ => { }, Token);
        Assert.Equal(2, checks);
        Assert.Equal(WorkflowOfficeSetupStep.WaitingRoute, result.Step);
        Assert.Null(result.Route);
    }

    [Fact]
    public async Task ServiceAndDeviceIdRecoverWithinTheSameAttempt()
    {
        var reads = 0;
        var nodeReads = 0;
        var states = new List<WorkflowOfficeSetupState>();
        var setup = new WorkflowOfficeSetup((_, _, _) => Task.FromResult(new WorkflowOfficeProbe(WorkflowOfficeHealth.Reachable, Route)),
            (_, _) => ++reads switch
            {
                1 => throw new ZeroTierServiceUnavailableException("Сбой", new IOException()),
                2 or 3 => Task.FromResult(new ZeroTierNetwork("ACCESS_DENIED")),
                _ => Task.FromResult(new ZeroTierNetwork("OK", Binding))
            }, (_, _) => throw new InvalidOperationException(),
            _ => ++nodeReads == 1 ? throw new IOException() : Task.FromResult("0123456789"), TimeSpan.Zero, 5);
        var result = await setup.PrepareAsync(Server, NetworkId, true, states.Add, Token);
        Assert.Equal(Route, result.Route);
        Assert.Contains(states, state => state.Step == WorkflowOfficeSetupStep.WaitingService);
        Assert.Contains(states, state => state.Step == WorkflowOfficeSetupStep.WaitingApproval && state.NodeId == "0123456789");
        Assert.Equal(2, nodeReads);
    }

    [Theory]
    [InlineData("10.0.0.0/24", null, true)]
    [InlineData("10.0.0.128/25", null, true)]
    [InlineData("10.0.0.0/24", "10.0.0.1", false)]
    [InlineData("192.168.10.0/24", null, false)]
    [InlineData("10.0.0.0/16", null, false)]
    [InlineData("invalid", null, false)]
    public async Task NewNetworkJoinsWithManagedRoutesOffAndOnlyEnablesThemForAnApprovedConfiguration(string target, string? via, bool approved)
    {
        var joined = false;
        var managed = false;
        var failures = 0;
        var states = new List<WorkflowOfficeSetupState>();
        var settingsWritten = new List<bool>();
        using var http = new HttpClient(new Handler(request =>
        {
            if (failures++ == 0) return new(HttpStatusCode.ServiceUnavailable);
            if (request.Method == HttpMethod.Post)
            {
                var json = JsonSerializer.SerializeToElement(((JsonContent)request.Content!).Value);
                managed = json.GetProperty("allowManaged").GetBoolean();
                settingsWritten.Add(managed);
                joined = true;
                return new(HttpStatusCode.OK);
            }
            if (!joined) return new(HttpStatusCode.NotFound);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new
                {
                    nwid = NetworkId, status = "OK", type = "PRIVATE", allowManaged = managed,
                    allowDefault = false, allowGlobal = false, allowDNS = false, mac = "11:22:33:44:55:66",
                    assignedAddresses = new[] { "10.0.0.3/24" },
                    routes = new[] { new { target, via } }
                })
            };
        })) { BaseAddress = new("http://127.0.0.1:9993/") };
        var zt = new ZeroTierClient(http, _ => Task.FromResult("test-token"));
        var setup = new WorkflowOfficeSetup((_, _, _) => Task.FromResult(new WorkflowOfficeProbe(WorkflowOfficeHealth.Reachable, ObservedRoute)),
            (id, token) => managed ? Task.FromResult(new ZeroTierNetwork("OK", Binding)) : zt.ReadAsync(id, token),
            zt.RequestRestrictedJoinAsync, _ => throw new InvalidOperationException(), TimeSpan.Zero, 5, zt.EnableManagedAddressesAsync);

        var result = await setup.PrepareAsync(Server, NetworkId, true, states.Add, Token);

        Assert.Contains(states, state => state.Step == WorkflowOfficeSetupStep.WaitingService);
        Assert.Equal(approved ? new[] { false, true } : new[] { false }, settingsWritten);
        Assert.Equal(approved ? WorkflowOfficeSetupStep.Ready : WorkflowOfficeSetupStep.Error, result.Step);
    }

    [Fact]
    public async Task ExistingNetworkWithManagedAddressesOffRequiresExplicitConfirmation()
    {
        var enabled = false;
        var setup = new WorkflowOfficeSetup((_, _, _) => Task.FromResult(new WorkflowOfficeProbe(WorkflowOfficeHealth.Reachable, ObservedRoute)),
            (_, _) => Task.FromResult(enabled ? new ZeroTierNetwork("OK", Binding) :
                new("ADDRESS_PENDING") { ManagedAddressesDisabled = true }),
            (_, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException(), TimeSpan.Zero,
            enableManagedAddresses: (_, _) => { enabled = true; return Task.CompletedTask; });

        var awaiting = await setup.PrepareAsync(Server, NetworkId, true, _ => { }, Token);
        Assert.Equal(WorkflowOfficeSetupStep.EnableManagedAddresses, awaiting.Step);
        Assert.False(enabled);
        var confirmed = await setup.PrepareAsync(Server, NetworkId, true, _ => { }, Token, confirmManagedAddresses: true);
        Assert.Equal(WorkflowOfficeSetupStep.Ready, confirmed.Step);
    }

    [Fact]
    public async Task AuthorizationCarriesTheVerifiedRouteAndUsesAFreshConnection()
    {
        var key = new string('A', 64);
        var databaseId = Guid.NewGuid();
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/storage-connect", request.RequestUri!.AbsolutePath);
            Assert.True(request.Options.TryGetValue(WorkflowOfficeTransport.ExpectedRoute, out var expected));
            Assert.Equal(ObservedRoute, expected);
            Assert.True(request.Headers.ConnectionClose);
            Assert.Equal(key, request.Headers.Authorization?.Parameter);
            return new(HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(new { databaseId, token = "storage.example", canWrite = true }) };
        }));
        var office = await WorkflowOfficeStorageClient.ConnectAsync(Server.AbsoluteUri, key, Path.GetTempPath(), ObservedRoute, Token, client);
        Assert.Equal(databaseId, office.Settings.DatabaseId);
        Assert.Equal(ObservedRoute.InterfaceId, office.Settings.ZeroTierInterfaceId);
    }

    [Theory]
    [InlineData("{\"status\":\"ok\"}", 200, "Reachable")]
    [InlineData("{\"status\":\"error\"}", 200, "UnexpectedResponse")]
    [InlineData("not json", 200, "UnexpectedResponse")]
    [InlineData("{\"status\":\"ok\"}", 302, "UnexpectedResponse")]
    public async Task ServerProbeUsesTheExistingHealthEndpointWithoutTheKey(string body, int status, string expected)
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(new Uri(Server, "health"), request.RequestUri);
            Assert.Null(request.Headers.Authorization);
            request.Options.Set(WorkflowOfficeTransport.ConnectedRoute, Route);
            return new((HttpStatusCode)status) { Content = new StringContent(body) };
        }));
        var result = await new WorkflowOfficeTransport(client).CheckHealthAsync(Server, Route, Token);
        Assert.Equal(expected, result.Health.ToString());
    }

    [Fact]
    public async Task NonZeroTierRouteIsDistinguishedFromAnUnreachableServer()
    {
        using var client = new HttpClient(new Handler(_ => throw new HttpRequestException("Wrapped", new WorkflowOfficeConnectException("Route", true))));
        var result = await new WorkflowOfficeTransport(client).CheckHealthAsync(Server, null, Token);
        Assert.Equal(WorkflowOfficeHealth.RouteMissing, result.Health);
    }

    [Theory]
    [InlineData("http://8.8.8.8:60913/")]
    [InlineData("http://localhost:60913/")]
    [InlineData("http://10.0.0.2:60913/api/v1/")]
    [InlineData("http://name@10.0.0.2:60913/")]
    public void OnlyPrivateServerAddressesAreAccepted(string address) =>
        Assert.Throws<WorkflowValidationException>(() => WorkflowOfficeTransport.RequireServer(address));

    private static WorkflowOfficeSetup NewSetup(WorkflowOfficeHealth health, WorkflowOfficeRoute? route = null) =>
        new((_, _, _) => Task.FromResult(new WorkflowOfficeProbe(health, route)),
        (_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(),
        _ => throw new InvalidOperationException(), TimeSpan.Zero);

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
