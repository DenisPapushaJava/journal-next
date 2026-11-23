using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal enum WorkflowOfficeHealth { Reachable, RouteMissing, Unavailable, UnexpectedResponse }
internal sealed record WorkflowOfficeRoute(IPAddress? Address, string? InterfaceName, string? InterfaceId = null)
{
    internal bool Matches(WorkflowOfficeRoute actual) =>
        (Address is null || Address.Equals(actual.Address)) && MatchesInterface(actual.InterfaceName, actual.InterfaceId);

    internal bool MatchesInterface(string? name, string? id) =>
        (InterfaceName is null || InterfaceName == name) && (InterfaceId is null || InterfaceId == id);
}
internal readonly record struct WorkflowOfficeProbe(WorkflowOfficeHealth Health, WorkflowOfficeRoute? Route = null);
internal sealed class WorkflowOfficeConnectException(string message, bool routeMissing, Exception? inner = null) : IOException(message, inner)
{
    internal bool RouteMissing { get; } = routeMissing;
}
internal sealed class WorkflowOfficeTransport(HttpClient http)
{
    private sealed record HealthResponse(string? Status);
    private const string NetworkHint = "Проверьте, что этот компьютер подключён к сети ZeroTier рабочего сервера.";
    internal static readonly HttpRequestOptionsKey<WorkflowOfficeRoute> ExpectedRoute = new("OfficeZeroTierRoute");
    internal static readonly HttpRequestOptionsKey<WorkflowOfficeRoute> ConnectedRoute = new("OfficeZeroTierConnectedRoute");
    internal static readonly HttpClient ConnectionClient = CreateClient(TimeSpan.FromSeconds(20));

    internal static HttpClient CreateClient(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectCallback = ConnectAsync,
        PooledConnectionLifetime = TimeSpan.Zero
    })
    {
        Timeout = timeout,
        MaxResponseContentBufferSize = 256 * 1024
    };

    internal WorkflowOfficeTransport() : this(ConnectionClient) { }

    internal static void UseRoute(HttpRequestMessage request, WorkflowOfficeRoute? route)
    {
        if (route is null) return;
        request.Options.Set(ExpectedRoute, route);
        request.Headers.ConnectionClose = true;
    }

    internal async Task<WorkflowOfficeProbe> CheckHealthAsync(Uri server, WorkflowOfficeRoute? route, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server, "health"));
        request.Headers.ConnectionClose = true;
        UseRoute(request, route);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK) return new(WorkflowOfficeHealth.UnexpectedResponse);
            await response.Content.LoadIntoBufferAsync(4096, deadline.Token);
            var body = await response.Content.ReadFromJsonAsync<HealthResponse>(deadline.Token);
            if (body?.Status != "ok") return new(WorkflowOfficeHealth.UnexpectedResponse);
            return request.Options.TryGetValue(ConnectedRoute, out var connected)
                ? new(WorkflowOfficeHealth.Reachable, connected) : new(WorkflowOfficeHealth.RouteMissing);
        }
        catch (HttpRequestException ex) when (ex.InnerException is WorkflowOfficeConnectException { RouteMissing: true })
        { return new(WorkflowOfficeHealth.RouteMissing); }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex is OperationCanceledException && !token.IsCancellationRequested)
        { return new(WorkflowOfficeHealth.Unavailable); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        { return new(WorkflowOfficeHealth.UnexpectedResponse); }
    }

    internal static Uri RequireServer(string? address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var server) ||
            server.Scheme != Uri.UriSchemeHttp || server.HostNameType != UriHostNameType.IPv4 ||
            !IPAddress.TryParse(server.Host, out var ip) || !ZeroTierClient.IsPrivateIPv4(ip) ||
            server.UserInfo.Length > 0 || server.AbsolutePath != "/" || server.Query.Length > 0 || server.Fragment.Length > 0)
            throw new WorkflowValidationException("Вставьте приватный адрес с портом из панели «Сервер заявок».");
        return server;
    }

    internal static string RequireAdminKey(string? key)
    {
        key = key?.Trim();
        if (key is not { Length: 64 } || !key.All(Uri.IsHexDigit))
            throw new WorkflowValidationException("Скопируйте ключ кнопкой «Ключ администратора» в панели сервера.");
        return key;
    }

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var remote = new IPEndPoint(IPAddress.Parse(context.DnsEndPoint.Host), context.DnsEndPoint.Port);
        try
        {
            var request = context.InitialRequestMessage;
            request.Options.TryGetValue(ExpectedRoute, out var expected);
            var legacyLinux = OperatingSystem.IsLinux() && expected?.InterfaceId?.Contains('/') == true;
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            var source = expected?.Address;
            NetworkInterface adapter;
            if (expected is { InterfaceId: not null } or { InterfaceName: not null })
            {
                adapter = RequireSingleAdapter(adapters.Where(candidate =>
                    expected.MatchesInterface(candidate.Name, InterfaceIdentity(candidate, legacyLinux))));
            }
            else
            {
                // UDP connect selects the system route without sending a packet.
                using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                route.Connect(remote);
                var local = ((IPEndPoint)route.LocalEndPoint!).Address;
                if (source is not null && !source.Equals(local))
                    throw new WorkflowOfficeConnectException(NetworkHint, routeMissing: true);
                source = local;
                adapter = RequireSingleAdapter(adapters.Where(candidate => HasAddress(candidate, local)));
            }

            var zeroTier = OperatingSystem.IsLinux()
                ? adapter.Name.StartsWith("zt", StringComparison.Ordinal) || legacyLinux
                : adapter.Description.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase);
            if (!zeroTier || source is not null && !HasAddress(adapter, source))
                throw new WorkflowOfficeConnectException(NetworkHint, routeMissing: true);

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                BindInterface(socket, adapter);
                if (source is not null) socket.Bind(new IPEndPoint(source, 0));
                await socket.ConnectAsync(remote, token);
                var local = ((IPEndPoint)socket.LocalEndPoint!).Address;
                var observed = new WorkflowOfficeRoute(local, adapter.Name, InterfaceIdentity(adapter, legacyLinux));
                if (!HasAddress(adapter, local) || expected is not null && !expected.Matches(observed))
                    throw new WorkflowOfficeConnectException(NetworkHint, routeMissing: true);
                request.Options.Set(ConnectedRoute, observed);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.NetworkDown or
            SocketError.NetworkUnreachable or SocketError.AddressNotAvailable)
        { throw new WorkflowOfficeConnectException(NetworkHint, true, ex); }
        catch (SocketException ex)
        { throw new WorkflowOfficeConnectException("Не удалось соединиться с сервером заявок. Проверьте адрес и сеть ZeroTier.", false, ex); }
        catch (NetworkInformationException ex)
        { throw new WorkflowOfficeConnectException(NetworkHint, true, ex); }
    }

    private static NetworkInterface RequireSingleAdapter(IEnumerable<NetworkInterface> adapters) =>
        adapters.Take(2).ToArray() is [var adapter] ? adapter :
            throw new WorkflowOfficeConnectException(NetworkHint, routeMissing: true);

    private static bool HasAddress(NetworkInterface adapter, IPAddress address) =>
        adapter.GetIPProperties().UnicastAddresses.Any(candidate => candidate.Address.Equals(address));

    // Linux can reuse a generic interface name for another device. Include the MAC for verified legacy names.
    internal static string InterfaceIdentity(NetworkInterface adapter, bool includeMac) =>
        includeMac ? $"{adapter.Id}/{adapter.GetPhysicalAddress()}" : adapter.Id;

    private static void BindInterface(Socket socket, NetworkInterface adapter)
    {
        if (adapter.GetIPProperties().GetIPv4Properties() is not { Index: > 0 } ipv4)
            throw new WorkflowOfficeConnectException(NetworkHint, routeMissing: true);

        // Constrain the outgoing interface even if the system route changes.
        if (OperatingSystem.IsWindows())
            socket.SetRawSocketOption((int)SocketOptionLevel.IP, 31 /* IP_UNICAST_IF */,
                BitConverter.GetBytes(IPAddress.HostToNetworkOrder(ipv4.Index)));
        else if (OperatingSystem.IsLinux())
            socket.SetRawSocketOption(1 /* SOL_SOCKET */, 62 /* SO_BINDTOIFINDEX */, BitConverter.GetBytes(ipv4.Index));
        else
            throw new WorkflowOfficeConnectException(NetworkHint, routeMissing: true);
    }
}
