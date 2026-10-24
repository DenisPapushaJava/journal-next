using System.ComponentModel;
using Jornal.Core.Orders;

namespace Jornal.Infrastructure;

internal static class DataSourceAccessErrors
{
    internal static string Unavailable(string path) =>
        $"База данных «{Path.GetFileName(path)}» недоступна. Проверьте подключение и повторите попытку.";

    internal static string Describe(string path, Exception exception, string fallback)
    {
        // Даже известная причина разрыва связи не доказывает исход начавшейся записи.
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is OrderWriteException { RequiresReload: true }) return fallback;

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
                return $"Нет доступа к базе данных «{Path.GetFileName(path)}». Проверьте права доступа.";
            if (current is FileNotFoundException or DirectoryNotFoundException) return Unavailable(path);
            var code = current is Win32Exception native ? native.NativeErrorCode :
                (uint)current.HResult >> 16 == 0x8007 ? current.HResult & 0xffff : 0;
            if (code is 5 or 65)
                return $"Нет доступа к базе данных «{Path.GetFileName(path)}». Проверьте права доступа.";
            if (code is 2 or 3 or 53 or 64 or 67 or 1222 or 1231 or 1232 or 1236 or 2250) return Unavailable(path);
            if (code is 32 or 33)
                return $"База данных «{Path.GetFileName(path)}» сейчас занята другой операцией. Повторите действие через несколько секунд.";
        }
        return fallback;
    }
}
