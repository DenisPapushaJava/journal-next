using System.Globalization;

namespace Jornal.Infrastructure.Access;

internal static class AccessPriceBackup
{
    internal static string Create(string path, CancellationToken cancellationToken, string operation = "price-expansion")
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backup = Path.GetFullPath(path) + ".before-" + operation + "-" +
            DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".bak";
        var partial = backup + ".partial";
        try
        {
            // До подключения Jet: Share Exclusive запрещает даже второе чтение из своего процесса.
            using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024]; int count;
                while ((count = source.Read(buffer)) != 0)
                { cancellationToken.ThrowIfCancellationRequested(); destination.Write(buffer, 0, count); }
                destination.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested(); File.Move(partial, backup); return backup;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
}
