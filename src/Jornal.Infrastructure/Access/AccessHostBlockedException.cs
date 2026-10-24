namespace Jornal.Infrastructure.Access;

/// <summary>Отказ Windows при запуске хоста или проверке его исполняемых компонентов.</summary>
public sealed class AccessHostBlockedException : Exception
{
    public int Code { get; }
    public static bool IsLaunchCode(int code) => code is 4551 or 1260 or 577;
    public static bool IsExitCode(int code) => code is unchecked((int)0xC0E90002) or unchecked((int)0xC0000428);

    public AccessHostBlockedException(int code, Exception? innerException = null)
        : base(Describe(code), innerException) => Code = code;

    public static bool Contains(Exception? exception)
    {
        for (; exception is not null; exception = exception.InnerException)
            if (exception is AccessHostBlockedException) return true;
        return false;
    }

    private static string Describe(int code)
    {
        if (!IsLaunchCode(code) && !IsExitCode(code)) throw new ArgumentOutOfRangeException(nameof(code));
        var reason = code is 577 or unchecked((int)0xC0000428)
            ? "Windows не смогла проверить подпись или целостность модуля работы с MDB."
            : "Windows заблокировала модуль работы с MDB политикой контроля приложений. Это может быть Smart App Control или правило организации.";
        var codeText = code < 0 ? $"0x{unchecked((uint)code):X8}" : code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return reason + " Код Windows: " + codeText + ". Откройте параметры безопасности Windows. На рабочем компьютере обратитесь к ИТ.";
    }
}
