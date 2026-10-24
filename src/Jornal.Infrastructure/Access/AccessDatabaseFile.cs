using System.Text;
using Jornal.Core.Orders;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Infrastructure.Access;

internal static class AccessDatabaseFile
{
    public static Task ValidateAsync(string path, CancellationToken cancellationToken) =>
        DiagnosticStages.RunAsync(DiagnosticStage.OpenDatabase, async () =>
    {
        if (!string.Equals(Path.GetExtension(path), ".mdb", StringComparison.OrdinalIgnoreCase))
            throw new OrderReadException("Выберите существующий файл Access с расширением .mdb.");
        // Только чтение заголовка с совместным доступом к существующей базе.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous);
        var header = new byte[32];
        var length = await stream.ReadAtLeastAsync(header, 20, throwOnEndOfStream: false, cancellationToken);
        if (length < 20 || Encoding.ASCII.GetString(header, 4, 16) != "Standard Jet DB\0")
            throw new OrderReadException("Файл не является поддерживаемой базой Jet MDB либо повреждён.");
    });
}
