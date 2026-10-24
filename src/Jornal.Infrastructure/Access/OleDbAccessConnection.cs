using System.Data.OleDb;
using System.Runtime.Versioning;
using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Access;

[SupportedOSPlatform("windows")]
internal static class OleDbAccessConnection
{
    public static OleDbConnection Create(string path, bool writable, bool exclusive = false)
    {
        var provider = OleDbOrderReader.FindProvider() ?? throw new OrderReadException(
            $"Не найден драйвер Access для приложения {(Environment.Is64BitProcess ? "x64" : "x86")}. Нужен совместимый Microsoft Access Runtime с учётом установленного Office.");
        var builder = new OleDbConnectionStringBuilder
        { Provider = provider, DataSource = Path.GetFullPath(path), PersistSecurityInfo = false };
        // Jet принимает Read для чтения и DB_MODE_READWRITE | DB_MODE_SHARE_DENY_NONE для записи.
        builder["Mode"] = exclusive ? "Share Exclusive" : writable ? 19 : "Read";
        builder["OLE DB Services"] = -2;
        return new(builder.ConnectionString);
    }
}
