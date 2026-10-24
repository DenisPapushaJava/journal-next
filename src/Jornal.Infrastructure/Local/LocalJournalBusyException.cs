using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Local;

// Отказ получить служебную блокировку до чтения/записи; данные ещё не изменялись.
internal sealed class LocalJournalBusyException(IOException innerException)
    : IOException("Локальная база сейчас занята другой операцией. Повторите действие через несколько секунд.", innerException)
{
    internal static bool IsSharingViolation(IOException exception) => exception.HResult is
        unchecked((int)0x80070020) or unchecked((int)0x80070021) ||
        OperatingSystem.IsLinux() && exception.HResult == 11;

    internal static bool Contains(Exception exception)
    {
        var found = false;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OrderWriteException { Failure: not OrderWriteFailure.NotSaved }) return false;
            if (current is LocalJournalBusyException) found = true;
        }
        return found;
    }
}
