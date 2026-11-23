using System.Buffers.Binary;
using System.Globalization;
using System.Net.NetworkInformation;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal static class WorkflowOfficeLinuxNetwork
{
    private const string StateDirectory = "/var/lib/zerotier-one";
    private const int MaxDeviceMapBytes = 64 * 1024;

    internal static async Task<WorkflowOfficeRoute?> FindRouteAsync(string networkId, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux() || !ZeroTierClient.IsNetworkId(networkId)) return null;
        networkId = networkId.ToLowerInvariant();
        if (!File.Exists(Path.Combine(StateDirectory, "networks.d", networkId + ".conf")))
            return null;

        // Read the public node address only. The service token and private identity stay untouched.
        var identity = new char[11];
        try
        {
            using var reader = File.OpenText(Path.Combine(StateDirectory, "identity.public"));
            if (await reader.ReadBlockAsync(identity, token) != identity.Length || identity[10] != ':') return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }

        if (!TryGetAdapter(networkId, identity.AsSpan(0, 10), out var name, out var mac)) return null;
        var mappedName = await ReadMappedNameAsync(networkId, token);
        var matching = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => (adapter.Name == mappedName || adapter.Name == name) &&
                adapter.GetPhysicalAddress().GetAddressBytes().SequenceEqual(mac) &&
                adapter.GetIPProperties().UnicastAddresses.Any(address => ZeroTierClient.IsPrivateIPv4(address.Address)))
            .ToArray();
        var adapter = matching.FirstOrDefault(value => value.Name == mappedName) ?? matching.SingleOrDefault();
        return adapter is null ? null :
            new(null, adapter.Name, WorkflowOfficeTransport.InterfaceIdentity(adapter, adapter.Name == mappedName));
    }

    private static async Task<string?> ReadMappedNameAsync(string networkId, CancellationToken token)
    {
        var path = Path.Combine(StateDirectory, "devicemap");
        if (!File.Exists(path)) return null;
        try
        {
            using var reader = File.OpenText(path);
            if (reader.BaseStream.Length > MaxDeviceMapBytes) return null;
            return FindMappedName(await reader.ReadToEndAsync(token), networkId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string? FindMappedName(string deviceMap, string networkId)
    {
        string? name = null;
        foreach (var line in deviceMap.Split('\n'))
        {
            if (line.Split('=') is not [var id, var value] || !id.Equals(networkId, StringComparison.OrdinalIgnoreCase)) continue;
            value = value.TrimEnd('\r');
            if (value is { Length: > 0 and <= 15 }) name = value;
        }
        return name;
    }

    internal static bool TryGetAdapter(string networkId, ReadOnlySpan<char> nodeAddress, out string name, out byte[] mac)
    {
        name = "";
        mac = [];
        if (!ZeroTierClient.IsNetworkId(networkId) || nodeAddress.Length != 10 ||
            !ulong.TryParse(nodeAddress, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var node))
            return false;
        var network = ulong.Parse(networkId, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

        // ZeroTier One derives the default Linux TAP name and the MAC from the network and node IDs.
        // Match the MAC even when devicemap supplies a different name.
        const string base32 = "abcdefghijklmnopqrstuvwxyz234567";
        var nameBits = network ^ (network >> 24);
        Span<char> suffix = stackalloc char[8];
        for (var i = suffix.Length - 1; i >= 0; i--)
        {
            suffix[i] = base32[(int)(nameBits & 31)];
            nameBits >>= 5;
        }
        name = string.Concat("zt", suffix);

        Span<byte> networkBytes = stackalloc byte[8];
        Span<byte> nodeBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(networkBytes, network);
        BinaryPrimitives.WriteUInt64BigEndian(nodeBytes, node);
        mac = new byte[6];
        mac[0] = (byte)((networkBytes[7] & 0xfe) | 2);
        if (mac[0] == 0x52) mac[0] = 0x32;
        for (var i = 1; i < mac.Length; i++)
            mac[i] = (byte)(nodeBytes[i + 2] ^ networkBytes[7 - i]);
        return true;
    }
}
