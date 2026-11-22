using System.Globalization;
using System.Net;
using System.Security.Cryptography;

namespace Jornal.Workflow.Server.Windows;

internal static class ZeroTierWindowsSetup
{
    private const string InstallerUrl = "https://download.zerotier.com/RELEASES/1.16.2/dist/ZeroTierOne.msi";

    // ZeroTier.ZeroTierOne 1.16.2 in the Windows Package Manager catalog.
    internal const string InstallerSha256 = "42514072B0FE44B8F66E0395BCD23A0B1D1642C28ED00831F1527B2F41B14670";
    internal static string RuleMarker => Path.Combine(OfficeInstallation.DirectoryPath, "firewall-rule.managed");

    public static bool ClientInstalled => File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ZeroTier", "One", "zerotier-one_x64.exe"));

    public static async Task InstallClientAsync()
    {
        if (ClientInstalled) return;
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
        client.MaxResponseContentBufferSize = 20 * 1024 * 1024;
        var bytes = await client.GetByteArrayAsync(InstallerUrl);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != InstallerSha256)
            throw new InvalidDataException("Не удалось подтвердить целостность установщика ZeroTier. Скачайте его с сайта ZeroTier.");
        var directory = Directory.CreateTempSubdirectory("journal-zerotier-");
        var path = Path.Combine(directory.FullName, "ZeroTierOne.msi");
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            var result = await OfficeInstallation.RunElevatedAsync("msiexec.exe", "/i", path, "/passive", "/norestart");
            if (result == 3010)
                throw new InvalidOperationException("ZeroTier установлен. Перезагрузите Windows и снова откройте «Сервер заявок».");
            if (result != 0) throw new InvalidOperationException($"Установка ZeroTier завершилась с кодом {result}.");
        }
        finally { directory.Delete(recursive: true); }
    }

    public static async Task AllowApiAsync(ZeroTierBinding binding, int port)
    {
        File.WriteAllText(RuleMarker, "Journal Office Server");
        var parameters = "-Direction Inbound -Action Allow -Enabled True -Protocol TCP " +
            $"-Program {Quote(OfficeInstallation.Executable)} -LocalPort {port.ToString(CultureInfo.InvariantCulture)} " +
            $"-LocalAddress {Quote(binding.Address.ToString())} -RemoteAddress {Quote(Subnet(binding))} " +
            $"-InterfaceAlias {Quote(WildcardLiteral(binding.InterfaceAlias))} -EdgeTraversalPolicy Block";
        var name = Quote("JournalOfficeServer-ZeroTier-" + port.ToString(CultureInfo.InvariantCulture));
        var command = "$ErrorActionPreference='Stop'; " +
            $"if (Get-NetFirewallRule -PolicyStore PersistentStore -Name {name} -ErrorAction SilentlyContinue) {{ " +
            $"Set-NetFirewallRule -PolicyStore PersistentStore -Name {name} {parameters} " +
            $"}} else {{ New-NetFirewallRule -PolicyStore PersistentStore -Name {name} " +
            $"-DisplayName 'Journal Office Server - ZeroTier' {parameters} | Out-Null }}";
        var result = await OfficeInstallation.RunElevatedPowerShellAsync(command);
        if (result != 0)
            throw new InvalidOperationException("Windows не применила правило для порта «Заявок». Проверьте разрешения брандмауэра.");
    }

    public static async Task RemoveApiRulesAsync()
    {
        var executable = Quote(OfficeInstallation.Executable);
        var command = "$ErrorActionPreference='Stop'; " +
            "Get-NetFirewallRule -PolicyStore PersistentStore | " +
            "Where-Object { $_.Name -match '^JournalOfficeServer-ZeroTier-[0-9]+$' } | " +
            $"Where-Object {{ ($_ | Get-NetFirewallApplicationFilter).Program -ieq {executable} }} | " +
            "Remove-NetFirewallRule";
        if (await OfficeInstallation.RunElevatedPowerShellAsync(command) != 0)
            throw new InvalidOperationException("Не удалось удалить правило «Сервера заявок» из брандмауэра Windows.");
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string WildcardLiteral(string value) => value.Replace("`", "``", StringComparison.Ordinal)
        .Replace("[", "`[", StringComparison.Ordinal).Replace("]", "`]", StringComparison.Ordinal)
        .Replace("*", "`*", StringComparison.Ordinal).Replace("?", "`?", StringComparison.Ordinal);

    internal static string Subnet(ZeroTierBinding binding) =>
        new IPNetwork(IPNetworkMask(binding.Address, binding.PrefixLength), binding.PrefixLength).ToString();

    private static IPAddress IPNetworkMask(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] &= (byte)(0xff << (8 - Math.Clamp(prefixLength - i * 8, 0, 8)));
        return new(bytes);
    }
}
