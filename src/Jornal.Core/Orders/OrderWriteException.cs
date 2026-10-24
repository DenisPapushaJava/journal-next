namespace Jornal.Core.Orders;

public enum OrderWriteFailure { NotSaved, Conflict, OutcomeUnknown }

public sealed class OrderWriteException(OrderWriteFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public OrderWriteFailure Failure { get; } = failure;
    public bool RequiresReload => Failure is OrderWriteFailure.Conflict or OrderWriteFailure.OutcomeUnknown;

    public static OrderWriteException Conflict() => new(OrderWriteFailure.Conflict,
        "Одна из заявок удалена или её значение уже изменено другим клиентом. Пакет не сохранён. Закройте форму БЛЦБ, если она открыта, и обновите список (F5).");
}
