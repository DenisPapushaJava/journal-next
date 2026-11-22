using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Jornal.Workflow.Server.Windows;

internal sealed record ZeroTierBinding(IPAddress Address, string InterfaceAlias, int PrefixLength);

internal sealed record ZeroTierNetwork(string Status, ZeroTierBinding? Binding = null)
{
    public bool CanWait => Status is "REQUESTING_CONFIGURATION" or "ACCESS_DENIED" or "ADDRESS_PENDING";

    public string Description => Status switch
    {
        "NOT_JOINED" => "Нажмите «Подключить», чтобы отправить запрос в ZeroTier.",
        "ACCESS_DENIED" => "Разрешите устройство в ZeroTier Central.",
        "REQUESTING_CONFIGURATION" => "ZeroTier запрашивает настройки сети…",
        "ADDRESS_PENDING" => "Ожидается приватный IPv4-адрес. Проверьте выдачу адресов в ZeroTier Central.",
        "PUBLIC" => "Для заявок нужна приватная сеть ZeroTier.",
        "ROUTING" => "Выберите приватную сеть без шлюза, DNS и маршрутов в другие подсети.",
        "PORT_ERROR" => "ZeroTier не смог запустить сетевой адаптер. Проверьте установку клиента.",
        "NOT_FOUND" => "Сеть ZeroTier не найдена. Проверьте Network ID.",
        "CLIENT_TOO_OLD" => "Обновите клиент ZeroTier.",
        "OK" => "Приватная сеть ZeroTier подключена.",
        _ => "ZeroTier пока не подтвердил подключение к сети."
    };
}

internal sealed class ZeroTierClient
{
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
    {
        BaseAddress = new("http://127.0.0.1:9993/"),
        Timeout = TimeSpan.FromSeconds(4)
    };

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

    public async Task RequestJoinAsync(string networkId, CancellationToken token)
    {
        if ((await ReadAsync(networkId, token)).Status != "NOT_JOINED") return;
        using var response = await SendAsync(HttpMethod.Post, NetworkPath(networkId), token,
            JsonContent.Create(new { allowManaged = true, allowGlobal = false, allowDefault = false, allowDNS = false }));
        response.EnsureSuccessStatusCode();
    }

    internal static ZeroTierNetwork ParseNetwork(JsonElement network, string networkId, Func<IPAddress, string, ZeroTierBinding?> findBinding)
    {
        if (network.ValueKind != JsonValueKind.Object || !string.Equals(Text(network, "nwid"), networkId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZeroTier вернул данные другой сети.");
        var status = Text(network, "status");
        if (status != "OK") return new(status ?? "");
        if (Text(network, "type") != "PRIVATE") return new("PUBLIC");
        if (!IsOff(network, "allowDefault") || !IsOff(network, "allowGlobal") || !IsOff(network, "allowDNS") ||
            !network.TryGetProperty("routes", out var routes) || routes.ValueKind != JsonValueKind.Array ||
            routes.EnumerateArray().Any(route => route.ValueKind != JsonValueKind.Object || !string.IsNullOrEmpty(Text(route, "via"))))
            return new("ROUTING");

        var mac = Text(network, "mac")?.Replace(":", "", StringComparison.Ordinal);
        if (mac is not { Length: 12 } || !mac.All(Uri.IsHexDigit) || !network.TryGetProperty("assignedAddresses", out var addresses) ||
            addresses.ValueKind != JsonValueKind.Array)
            return new("ADDRESS_PENDING");
        foreach (var assignment in addresses.EnumerateArray())
        {
            var value = assignment.ValueKind == JsonValueKind.String ? assignment.GetString() : null;
            var addressPart = value?.Split('/', 2)[0];
            if (IPAddress.TryParse(addressPart, out var address) && IsPrivateIPv4(address) &&
                findBinding(address, mac) is { } binding)
                return new("OK", binding);
        }
        return new("ADDRESS_PENDING");
    }

    internal static bool IsPrivateIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168;
    }

    private static ZeroTierBinding? FindBinding(IPAddress address, string mac)
    {
        var minimumPrefix = address.GetAddressBytes()[0] switch { 10 => 8, 172 => 12, _ => 16 };
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

    private static async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add("X-ZT1-Auth", await ReadUserTokenAsync(token));
        try
        {
            var response = await Client.SendAsync(request, token);
            if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return response;
            response.Dispose();
            throw new UnauthorizedAccessException("ZeroTier отклонил локальный доступ. Откройте ZeroTier One под этим пользователем Windows.");
        }
        catch (HttpRequestException exception)
        {
            throw new IOException("Служба ZeroTier недоступна. Запустите или установите ZeroTier One.", exception);
        }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        {
            throw new IOException("Служба ZeroTier не ответила. Проверьте ZeroTier One.", exception);
        }
    }

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
        throw new UnauthorizedAccessException("Откройте ZeroTier One под этим пользователем Windows. Если ZeroTier ещё не установлен, нажмите «Установить ZeroTier».");
    }

    private static string NetworkPath(string networkId) => IsNetworkId(networkId)
        ? "network/" + networkId
        : throw new ArgumentException("Network ID состоит из 16 шестнадцатеричных символов.");

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsOff(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;
}
