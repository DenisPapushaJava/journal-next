namespace Jornal.Core.Orders;

// Сообщение предназначено пользователю; подробности провайдера остаются во внутреннем исключении.
public sealed class OrderReadException(string message, Exception? innerException = null)
    : Exception(message, innerException);
