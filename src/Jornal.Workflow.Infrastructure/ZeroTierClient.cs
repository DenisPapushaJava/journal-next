using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Jornal.Workflow.Infrastructure;

public sealed record ZeroTierBinding(IPAddress Address, string InterfaceAlias, int PrefixLength);
public sealed class ZeroTierServiceUnavailableException(string message, Exception inner) : IOException(message, inner);

public sealed record ZeroTierNetwork(string Status, ZeroTierBinding? Binding = null)
{
    public bool CanWait => Status is "REQUESTING_CONFIGURATION" or "ACCESS_DENIED" or "ADDRESS_PENDING";
    public bool ManagedAddressesDisabled { get; init; }

    public string Description => Status switch
    {
        "NOT_JOINED" => "Нажмите «Подключить», чтобы отправить запрос в ZeroTier.",
        "ACCESS_DENIED" => "Разрешите устройство в ZeroTier Central.",
        "REQUESTING_CONFIGURATION" => "ZeroTier запрашивает настройки сети…",
        "ADDRESS_PENDING" => "Ожидается приватный IPv4-адрес. Проверьте выдачу адресов в ZeroTier Central.",
        "PUBLIC" => "Для заявок нужна приватная сеть ZeroTier.",
        "ROUTING" => "В ZeroTier Central проверьте маршруты сети. Допустимы прямые маршруты внутри подсетей назначенных адресов.",
        "PORT_ERROR" => "ZeroTier не смог запустить сетевой адаптер. Проверьте установку клиента.",
        "NOT_FOUND" => "Сеть ZeroTier не найдена. Проверьте Network ID.",
        "CLIENT_TOO_OLD" => "Обновите клиент ZeroTier.",
        "OK" => "Приватная сеть ZeroTier подключена.",
        _ => "ZeroTier пока не подтвердил подключение к сети."
    };
}

public sealed class ZeroTierClient
{
    private static readonly HttpClient ServiceClient = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
    {
        BaseAddress = new("http://127.0.0.1:9993/"),
        Timeout = TimeSpan.FromSeconds(4),
        MaxResponseContentBufferSize = 128 * 1024
    };
    private readonly HttpClient _client;
    private readonly Func<CancellationToken, Task<string>> _readToken;

    public ZeroTierClient() : this(ServiceClient, ReadUserTokenAsync) { }

    internal ZeroTierClient(HttpClient client, Func<CancellationToken, Task<string>> readToken)
    {
        _client = client;
        _readToken = readToken;
    }

    public const string CentralAddress = "https://central.zerotier.com/";

    public static bool IsNetworkId(string networkId) => networkId.Length == 16 && networkId.All(Uri.IsHexDigit);

    public async Task<ZeroTierNetwork> ReadAsync(string networkId, CancellationToken token = default)
    {
        using var response = await SendAsync(HttpMethod.Get, NetworkPath(networkId), token);
        if (response.StatusCode == HttpStatusCode.NotFound) return new("NOT_JOINED");
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        return ParseNetwork(json.RootElement, networkId, FindBinding);
    }

    public Task RequestJoinAsync(string networkId, CancellationToken token) => JoinAsync(networkId, true, token);

    public Task<bool> RequestRestrictedJoinAsync(string networkId, CancellationToken token) => JoinAsync(networkId, false, token);

    private async Task<bool> JoinAsync(string networkId, bool allowManaged, CancellationToken token)
    {
        if ((await ReadAsync(networkId, token)).Status != "NOT_JOINED") return false;
        using var response = await SendAsync(HttpMethod.Post, NetworkPath(networkId), token,
            JsonContent.Create(new { allowManaged, allowGlobal = false, allowDefault = false, allowDNS = false }));
        response.EnsureSuccessStatusCode();
        return true;
    }

    public async Task EnableManagedAddressesAsync(string networkId, CancellationToken token)
    {
        if (!(await ReadAsync(networkId, token)).ManagedAddressesDisabled) return;
        using var response = await SendAsync(HttpMethod.Post, NetworkPath(networkId), token, JsonContent.Create(new { allowManaged = true }));
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> ReadNodeIdAsync(CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get, "status", token);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        if (json.RootElement.ValueKind == JsonValueKind.Object && Text(json.RootElement, "address") is { Length: 10 } id && id.All(Uri.IsHexDigit))
            return id;
        throw new InvalidDataException("Не удалось определить ID устройства ZeroTier.");
    }

    internal static ZeroTierNetwork ParseNetwork(JsonElement network, string networkId, Func<IPAddress, string, ZeroTierBinding?> findBinding)
    {
        if (network.ValueKind != JsonValueKind.Object || !string.Equals(Text(network, "nwid"), networkId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZeroTier вернул данные другой сети.");
        var status = Text(network, "status");
        if (status != "OK") return new(status ?? "");
        if (Text(network, "type") != "PRIVATE") return new("PUBLIC");
        if (!IsOff(network, "allowDefault") || !IsOff(network, "allowGlobal") || !IsOff(network, "allowDNS") ||
            !network.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array)
            return new("ROUTING");

        var mac = Text(network, "mac")?.Replace(":", "", StringComparison.Ordinal);
        if (mac is not { Length: 12 } || !mac.All(Uri.IsHexDigit) || !network.TryGetProperty("assignedAddresses", out var addresses) ||
            addresses.ValueKind != JsonValueKind.Array)
            return new("ADDRESS_PENDING");
        var assigned = new List<(IPAddress Address, IPNetwork Subnet)>();
        foreach (var address in addresses.EnumerateArray())
            if (ReadAssignedAddress(address) is { } entry) assigned.Add(entry);
        if (routes.EnumerateArray().Any(route => !IsLocalRoute(route, assigned)))
            return new("ROUTING");
        foreach (var (address, _) in assigned)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) continue;
            if (IsOff(network, "allowManaged")) return new("ADDRESS_PENDING") { ManagedAddressesDisabled = true };
            if (findBinding(address, mac) is { } binding)
                return new("OK", binding);
        }
        return new("ADDRESS_PENDING");
    }

    private static (IPAddress Address, IPNetwork Subnet)? ReadAssignedAddress(JsonElement assignment)
    {
        if (assignment.ValueKind != JsonValueKind.String ||
            assignment.GetString()!.Split('/', 2) is not [var host, var prefixText] ||
            !IPAddress.TryParse(host, out var address) || !int.TryParse(prefixText, out var prefix) ||
            prefix < MinimumPrivatePrefix(address) || prefix > (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
            return null;
        return (address, GetSubnet(address, prefix));
    }

    private static bool IsLocalRoute(JsonElement route, List<(IPAddress Address, IPNetwork Subnet)> assigned) =>
        route.ValueKind == JsonValueKind.Object &&
        (!route.TryGetProperty("via", out var via) || via.ValueKind == JsonValueKind.Null ||
            via.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(via.GetString())) &&
        IPNetwork.TryParse(Text(route, "target"), out var target) &&
        assigned.Any(entry => entry.Subnet.BaseAddress.AddressFamily == target.BaseAddress.AddressFamily &&
            entry.Subnet.PrefixLength <= target.PrefixLength && entry.Subnet.Contains(target.BaseAddress));

    public static IPNetwork GetSubnet(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] &= (byte)(0xff << (8 - Math.Clamp(prefixLength - i * 8, 0, 8)));
        return new(new IPAddress(bytes), prefixLength);
    }

    public static bool IsPrivateIPv4(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && MinimumPrivatePrefix(address) != int.MaxValue;

    private static int MinimumPrivatePrefix(IPAddress address) => address.GetAddressBytes() switch
    {
        [10, _, _, _] => 8,
        [172, >= 16 and <= 31, _, _] => 12,
        [192, 168, _, _] => 16,
        _ when address.IsIPv6UniqueLocal => 7,
        _ => int.MaxValue
    };

    private static ZeroTierBinding? FindBinding(IPAddress address, string mac)
    {
        var minimumPrefix = MinimumPrivatePrefix(address);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                !adapter.Description.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(adapter.GetPhysicalAddress().ToString(), mac, StringComparison.OrdinalIgnoreCase))
                continue;
            var assignment = adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(value => value.Address.Equals(address));
            if (assignment is { PrefixLength: <= 32 } && assignment.PrefixLength >= minimumPrefix)
                return new(address, adapter.Name, assignment.PrefixLength);
        }
        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-ZT1-Auth", await _readToken(token));
        try
        {
            var response = await _client.SendAsync(request, token);
            var status = response.StatusCode;
            var transient = IsTransientStatus(status);
            if (status is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) && !transient) return response;
            response.Dispose();
            if (transient)
                throw new HttpRequestException("Временный отказ локальной службы ZeroTier.", null, status);
            throw new UnauthorizedAccessException("ZeroTier отклонил локальный доступ. Откройте ZeroTier One под этим пользователем Windows.");
        }
        catch (HttpRequestException exception)
        {
            throw new ZeroTierServiceUnavailableException("Служба ZeroTier недоступна. Запустите или установите ZeroTier One.", exception);
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        {
            throw new ZeroTierServiceUnavailableException("Служба ZeroTier не ответила. Проверьте ZeroTier One.", exception);
        }
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static async Task<string> ReadUserTokenAsync(CancellationToken token)
    {
        var userCopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeroTier", "authtoken.secret");
        var serviceCopy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZeroTier", "One", "authtoken.secret");
        foreach (var path in new[] { userCopy, serviceCopy })
        {
            try
            {
                var text = (await File.ReadAllTextAsync(path, token)).Trim();
                if (text.Length > 0) return text;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException) { }
        }
        throw new UnauthorizedAccessException("Нет локального доступа к ZeroTier. Установите ZeroTier One или откройте его из меню «Пуск» под текущим пользователем Windows.");
    }

    private static string NetworkPath(string networkId) => IsNetworkId(networkId)
        ? "network/" + networkId
        : throw new ArgumentException("Network ID состоит из 16 шестнадцатеричных символов.");

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsOff(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;
}
