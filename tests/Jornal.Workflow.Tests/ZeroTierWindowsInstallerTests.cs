using System.Security.Cryptography;
using Jornal.Workflow.Infrastructure;
using Xunit;

namespace Jornal.Workflow.Tests;

public sealed class ZeroTierWindowsInstallerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheStagedBytesAreVerifiedBeforeRunningAndHeldWhileTheInstallerReads(bool tampered)
    {
        var directory = Directory.CreateTempSubdirectory("workflow-verified-package-");
        var path = Path.Combine(directory.FullName, "тестовый пакет.msi");
        var token = TestContext.Current.CancellationToken;
        var expected = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(path, tampered ? [3, 2, 1] : expected, token);
        var started = false;
        try
        {
            Task<bool> Run() => ZeroTierWindowsInstaller.RunVerifiedPackageAsync(path,
                Convert.ToHexString(SHA256.HashData(expected)), CheckPackageAsync, token);
            if (tampered) await Assert.ThrowsAsync<InvalidDataException>(Run);
            else Assert.False(await Run());
            Assert.Equal(!tampered, started);

            async Task<bool> CheckPackageAsync()
            {
                started = true;
                Assert.Equal(expected, await File.ReadAllBytesAsync(path, token));
                if (OperatingSystem.IsWindows())
                {
                    Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
                    Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
                }
                return false;
            }
        }
        finally { directory.Delete(recursive: true); }
    }
}
