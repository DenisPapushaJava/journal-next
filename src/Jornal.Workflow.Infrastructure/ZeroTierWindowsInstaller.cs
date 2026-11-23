using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public static class ZeroTierWindowsInstaller
{
    private const string InstallerUrl = "https://download.zerotier.com/RELEASES/1.16.2/dist/ZeroTierOne.msi";
    // ZeroTier.ZeroTierOne 1.16.2 in the Windows Package Manager catalog.
    private const string InstallerSha256 = "42514072B0FE44B8F66E0395BCD23A0B1D1642C28ED00831F1527B2F41B14670";
    private const int MaximumPackageSize = 20 * 1024 * 1024;
    private const int InvalidPackageExitCode = 40961;
    private static readonly object InstallationGate = new();
    private static Task<bool>? _installation;

    public static bool IsInstalled => OperatingSystem.IsWindows() &&
        new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }.Any(folder =>
            File.Exists(Path.Combine(Environment.GetFolderPath(folder), "ZeroTier", "One", "zerotier-one_x64.exe")));

    public static bool IsInstalling
    {
        get { lock (InstallationGate) return _installation is { IsCompleted: false }; }
    }

    // The caller can stop waiting. Once Windows has the MSI, the worker keeps its lease until setup exits.
    public static Task<bool> InstallAsync(CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        token.ThrowIfCancellationRequested();
        lock (InstallationGate)
        {
            if (_installation is not { IsCompleted: false })
            {
                if (_installation is { IsCompletedSuccessfully: true } && _installation.Result) return _installation;
                if (IsInstalled) return Task.FromResult(false);
                _installation = Task.Run(() => InstallCoreAsync(token));
                _ = _installation.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return _installation.WaitAsync(TimeSpan.FromMinutes(2), token);
        }
    }

    private static async Task<bool> InstallCoreAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(45), MaxResponseContentBufferSize = MaximumPackageSize };
        var bytes = await client.GetByteArrayAsync(InstallerUrl, token).ConfigureAwait(false);
        var directory = Directory.CreateTempSubdirectory("journal-zerotier-");
        var path = Path.Combine(directory.FullName, "ZeroTierOne.msi");
        try
        {
            ShopFileSystem.RequireOrdinaryPath(path);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
                await output.WriteAsync(bytes, token).ConfigureAwait(false);
            return await RunVerifiedPackageAsync(path, InstallerSha256, () => RunInstallerAsync(path), token).ConfigureAwait(false);
        }
        finally
        {
            try { ShopFileSystem.RequireOrdinaryPath(path); File.Delete(path); directory.Delete(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or WorkflowValidationException) { }
        }
    }

    internal static async Task<bool> RunVerifiedPackageAsync(string path, string expectedSha256, Func<Task<bool>> run, CancellationToken token)
    {
        // A read lease allows MSI to read the package while Windows denies writes, delete and rename.
        ShopFileSystem.RequireOrdinaryPath(path);
        await using var package = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        ShopFileSystem.RequireOrdinaryPath(path);
        if (package.Length > MaximumPackageSize || !Convert.ToHexString(await SHA256.HashDataAsync(package, token).ConfigureAwait(false))
                .Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Целостность установщика ZeroTier не подтверждена. Скачайте ZeroTier с официального сайта.");
        token.ThrowIfCancellationRequested();
        return await run().ConfigureAwait(false);
    }

    private static async Task<bool> RunInstallerAsync(string path)
    {
        try
        {
            // Stage and verify a private copy in the elevated process, which can outlive the desktop application.
            var encodedPath = Convert.ToBase64String(Encoding.Unicode.GetBytes(path));
            var command = $$"""
                $ErrorActionPreference = 'Stop'
                $source = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{encodedPath}}'))
                $temp = [IO.Path]::Combine([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows), 'Temp')
                if ([IO.File]::GetAttributes($temp) -band [IO.FileAttributes]::ReparsePoint) {
                    throw 'Unexpected Windows temporary directory.'
                }
                $stage = [IO.Path]::Combine($temp, 'JournalZeroTier-' + [Guid]::NewGuid().ToString('N'))
                $security = [Security.AccessControl.DirectorySecurity]::new()
                # Administrators own the directory. Only Administrators and SYSTEM have access to it and its contents.
                $security.SetSecurityDescriptorSddlForm('O:BAD:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)')
                [void][IO.Directory]::CreateDirectory($stage, $security)
                $path = [IO.Path]::Combine($stage, 'ZeroTierOne.msi')
                try {
                    $inputFile = [IO.File]::Open($source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                    try {
                        if ($inputFile.Length -gt {{MaximumPackageSize}}) { exit {{InvalidPackageExitCode}} }
                        $outputFile = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                        try { $inputFile.CopyTo($outputFile) }
                        finally { $outputFile.Dispose() }
                    } finally { $inputFile.Dispose() }

                    $package = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                    try {
                        $sha = [Security.Cryptography.SHA256]::Create()
                        try {
                            if ([BitConverter]::ToString($sha.ComputeHash($package)).Replace('-', '') -cne '{{InstallerSha256}}') {
                                exit {{InvalidPackageExitCode}}
                            }
                        } finally { $sha.Dispose() }
                        $msi = [Diagnostics.ProcessStartInfo]::new()
                        $msi.FileName = [IO.Path]::Combine([Environment]::SystemDirectory, 'msiexec.exe')
                        $msi.WorkingDirectory = [Environment]::SystemDirectory
                        $msi.Arguments = '/i "' + $path + '" /passive /norestart'
                        $msi.UseShellExecute = $false
                        $process = [Diagnostics.Process]::Start($msi)
                        try { $process.WaitForExit(); exit $process.ExitCode }
                        finally { $process.Dispose() }
                    } finally { $package.Dispose() }
                } finally {
                    try { [IO.File]::Delete($path); [IO.Directory]::Delete($stage) }
                    catch [IO.IOException], [UnauthorizedAccessException] {}
                }
                """;
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand",
                         Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("Windows не запустила установщик ZeroTier.");
            await process.WaitForExitAsync().ConfigureAwait(false);
            return process.ExitCode switch
            {
                0 => false,
                1641 or 3010 => true,
                1602 => throw new InvalidOperationException("Установка ZeroTier отменена."),
                InvalidPackageExitCode => throw new InvalidDataException("Windows отклонила установщик ZeroTier: пакет изменился."),
                var code => throw new InvalidOperationException($"Установка ZeroTier завершилась с кодом {code}.")
            };
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { throw new InvalidOperationException("Установка ZeroTier отменена в запросе Windows.", error); }
    }
}
