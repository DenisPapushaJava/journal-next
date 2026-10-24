using System.Data.OleDb;
using System.Runtime.Versioning;

namespace Jornal.Infrastructure.Access;

[SupportedOSPlatform("windows")]
internal static class OrderDayQuery
{
    internal static string Where(OleDbCommand command, DateOnly? date, string field = "[Дата]")
    {
        if (date is not { } day) return "";
        command.Parameters.Add("dayStart", OleDbType.DBTimeStamp).Value = day.ToDateTime(TimeOnly.MinValue);
        // Последний допустимый день не имеет следующего дня в DateTime.
        if (day == DateOnly.MaxValue) return $" WHERE {field} >= ?";
        command.Parameters.Add("dayEnd", OleDbType.DBTimeStamp).Value = day.AddDays(1).ToDateTime(TimeOnly.MinValue);
        return $" WHERE {field} >= ? AND {field} < ?";
    }
}
