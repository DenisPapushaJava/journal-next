using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfficePreviewRow(string Number, string? Customer);
internal sealed record WorkflowOfficePreviewPage(int TotalCount, WorkflowOfficePreviewRow[] Orders);
internal sealed class WorkflowOfficePreviewClient(HttpClient http)
{
    private const string NetworkHint = "Проверьте, что этот компьютер подключён к сети ZeroTier рабочего сервера.";
    internal static readonly HttpClient Transport = CreateTransport(TimeSpan.FromSeconds(20));

    internal static HttpClient CreateTransport(TimeSpan timeout) => new(new SocketsHttpHandler
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

    public WorkflowOfficePreviewClient() : this(Transport) { }

    public async Task<WorkflowOfficePreviewPage> ReadAsync(string? address, string? key, CancellationToken token)
    {
        var page = await ReadJsonAsync<WorkflowOfficePreviewPage>(RequireServer(address), RequireAdminKey(key),
            "/api/v1/orders", null, token);
        if (page.Orders is not { } orders || page.TotalCount < orders.Length ||
            orders.Any(row => row is null || string.IsNullOrWhiteSpace(row.Number)))
            throw new InvalidDataException("Сервер вернул неполный список заявок.");
        return page;
    }

    internal static Uri RequireServer(string? address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var server) ||
            server.Scheme != Uri.UriSchemeHttp || server.HostNameType != UriHostNameType.IPv4 ||
            !IPAddress.TryParse(server.Host, out var ip) || !IsPrivate(ip) ||
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

    private async Task<T> ReadJsonAsync<T>(Uri server, string key, string path, HttpContent? body, CancellationToken token, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(server, path)) { Content = body };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
        {
            throw new WorkflowValidationException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when path == "/api/v1/orders" =>
                    "Сервер не принял ключ администратора. Скопируйте его заново.",
                HttpStatusCode.Unauthorized when path.StartsWith("/api/v1/work/", StringComparison.Ordinal) =>
                    "Срок подключения истёк или сервер сменил ключ. Подключитесь заново.",
                HttpStatusCode.Unauthorized => "Сервер не принял ключ администратора. Скопируйте его заново.",
                HttpStatusCode.Forbidden => "Технолог недоступен либо сервер запретил это действие.",
                HttpStatusCode.NotFound => "Сервер не нашёл данные. Проверьте версию сервера и обновите список заявок.",
                HttpStatusCode.Conflict => "Данные изменились или запись не подтверждена. Обновите список перед повторной попыткой.",
                HttpStatusCode.ServiceUnavailable when path == "/api/v1/technologists" =>
                    "Сервер не смог загрузить список технологов. Проверьте журнал сервера.",
                HttpStatusCode.ServiceUnavailable => "База заявок недоступна на сервере. Проверьте её состояние в панели сервера.",
                _ => $"Сервер не вернул список заявок (HTTP {(int)response.StatusCode})."
            });
        }

        return await response.Content.ReadFromJsonAsync<T>(token) ??
            throw new InvalidDataException("Сервер вернул пустой ответ.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        try { return await http.SendAsync(request, token); }
        catch (HttpRequestException ex) when (ex.InnerException is WorkflowValidationException validation)
        {
            throw new WorkflowValidationException(validation.Message);
        }
        catch (HttpRequestException)
        {
            throw new WorkflowValidationException("Сервер не ответил. Проверьте его адрес и соединение ZeroTier.");
        }
    }

    private static bool IsPrivate(IPAddress address) => address.GetAddressBytes() switch
    {
        [10, _, _, _] or [172, >= 16 and <= 31, _, _] or [192, 168, _, _] => true,
        _ => false
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var remote = new IPEndPoint(IPAddress.Parse(context.DnsEndPoint.Host), context.DnsEndPoint.Port);
        // UDP connect selects the system route without sending a packet.
        using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        route.Connect(remote);
        var local = ((IPEndPoint)route.LocalEndPoint!).Address;
        var throughZeroTier = NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            (OperatingSystem.IsLinux() ? adapter.Name.StartsWith("zt", StringComparison.Ordinal) :
                adapter.Description.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase)) &&
            adapter.GetIPProperties().UnicastAddresses.Any(address => address.Address.Equals(local)));
        if (!throughZeroTier) throw new WorkflowValidationException(NetworkHint);

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(local, 0));
            await socket.ConnectAsync(remote, token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
