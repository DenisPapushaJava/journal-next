using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jornal.Core.Orders;

// Ручные пометки в отдельной таблице. Привязка не зависит от станка/цены/веса.
public sealed record OrderAnnotations(bool HasBending, string Comment, string IdentityHash)
{
    public const int MaximumCommentLength = 255;
    public const string ReviewMessage = "Проверьте гибку и комментарий.";
    public const string UnavailableMessage = "Гибка и комментарии недоступны. Обновите список (F5).";
    public static void ValidateComment(string? value)
    {
        if (value is null || value.Length > MaximumCommentLength || value.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Комментарий — не более 255 символов, без служебных знаков.");
    }
    public void Validate()
    {
        ValidateComment(Comment);
        if (IdentityHash is not { Length: 64 } || !IdentityHash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Некорректная привязка пометок к заявке.");
    }
    public bool Matches(OrderSummary row) => IdentityHash == Fingerprint(row);
    public static OrderAnnotations? For(OrderSummary row, bool hasBending, string comment)
    {
        ValidateComment(comment);
        return !hasBending && comment.Length == 0 ? null : new(hasBending, comment, Fingerprint(row));
    }
    private static string Fingerprint(OrderSummary row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new[] { "Journal.Annotations.v1", row.OrderDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.SpecificationNumber }))));
}

public interface IOrderAnnotationsInitializer
{
    Task PrepareAnnotationsAsync(CancellationToken cancellationToken = default);
}
