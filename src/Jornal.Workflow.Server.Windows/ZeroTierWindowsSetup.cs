using System.Globalization;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Server.Windows;

internal static class ZeroTierWindowsSetup
{
    internal static string RuleMarker => Path.Combine(OfficeInstallation.DirectoryPath, "firewall-rule.managed");

    public static bool ClientInstalled => ZeroTierWindowsInstaller.IsInstalled;

    public static async Task InstallClientAsync()
    {
        if (await ZeroTierWindowsInstaller.InstallAsync())
            throw new InvalidOperationException("ZeroTier установлен. Перезагрузите Windows и снова откройте «Сервер заявок».");
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
        ZeroTierClient.GetSubnet(binding.Address, binding.PrefixLength).ToString();
}
