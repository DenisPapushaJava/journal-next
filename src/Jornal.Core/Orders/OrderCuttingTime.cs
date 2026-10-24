using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jornal.Core.Orders;

// Дополнение Journal: никогда не включается в прежнюю строку коэффициента.
public sealed record OrderCuttingTime(long Minutes, string ParametersHash)
{
    public static long MaximumMinutes => TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMinute;
    public void Validate()
    {
        if (Minutes < 0 || Minutes > MaximumMinutes || ParametersHash is not { Length: 64 } ||
            !ParametersHash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F'))
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Некорректное время резки или его привязка к заявке.");
    }
    public bool Matches(OrderSummary row) => ParametersHash == Fingerprint(row);
    public static OrderCuttingTime For(OrderSummary row, long minutes)
    { var value = new OrderCuttingTime(minutes, Fingerprint(row)); value.Validate(); return value; }

    private static string Fingerprint(OrderSummary row) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new string?[] { "Journal.CuttingTime.v1", row.StoredOrderDateTime?.ToString("O", CultureInfo.InvariantCulture)
            ?? row.OrderDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), row.SpecificationNumber, row.CuttingType,
            row.MaterialName, row.ThicknessMillimeters?.ToString("G29", CultureInfo.InvariantCulture), row.SheetSize, row.SheetCount,
            row.CutLengthMeters?.ToString(CultureInfo.InvariantCulture), row.WeightKilograms, row.StoredCoefficient }))));
}

public static class CuttingDuration
{
    public const string ValidationMessage = "Укажите время в формате ЧЧ:ММ.";
    public static bool TryParse(string text, out long? minutes)
    {
        minutes = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Trim().Split(':');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Any(c => c is < '0' or > '9')) ||
            parts[1].Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var remainder) || remainder > 59) return false;
        try
        {
            var result = checked(hours * 60 + remainder);
            if (result > OrderCuttingTime.MaximumMinutes) return false;
            minutes = result; return true;
        }
        catch (OverflowException) { return false; }
    }
    public static long RoundUpMinutes(TimeSpan value) => value.Ticks / TimeSpan.TicksPerMinute +
        (value.Ticks % TimeSpan.TicksPerMinute == 0 ? 0 : 1);
    public static string Format(long minutes) => (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
        (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
}

public interface IOrderDurationInitializer
{
    Task PrepareCuttingTimeAsync(CancellationToken cancellationToken = default);
}
