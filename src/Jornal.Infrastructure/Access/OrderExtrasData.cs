using System.Globalization;
using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Access;

public static class OrderExtrasData
{
    public const string TableName = "JournalOrderExtras";
    public static (int Id, OrderCuttingTime Time) Map(object id, object minutes, object hash, object version)
    {
        try
        {
            var key = Convert.ToDecimal(id, CultureInfo.InvariantCulture);
            var number = Convert.ToDecimal(minutes, CultureInfo.InvariantCulture);
            if (key != decimal.Truncate(key) || key <= 0 || number != decimal.Truncate(number) ||
                Convert.ToDecimal(version, CultureInfo.InvariantCulture) != 1) throw new FormatException();
            var result = new OrderCuttingTime(checked((long)number), (string)hash); result.Validate();
            return (checked((int)key), result);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or OrderWriteException)
        { throw new OrderReadException("Не удалось прочитать время резки. Неизвестное значение не заменено нулём.", exception); }
    }

    public static IReadOnlyDictionary<int, OrderCuttingTime> ReadExport(TextReader reader, CancellationToken token = default)
    {
        using var records = MdbToolsTableExport.ReadCsv(reader, token, tableName: TableName).GetEnumerator();
        string[] required = ["OrderId", "DurationMinutes", "ParametersHash", "FormatVersion"];
        if (!records.MoveNext()) throw new OrderReadException("Не прочитана структура таблицы времени.");
        var names = records.Current.Select(c => c.Text).ToArray();
        if (names.Length != 4 || !names.ToHashSet(StringComparer.Ordinal).SetEquals(required))
            throw new OrderReadException("Неизвестная структура таблицы времени резки.");
        var result = new Dictionary<int, OrderCuttingTime>();
        while (records.MoveNext())
        {
            if (records.Current.Count != 4) throw new OrderReadException("Повреждена строка времени резки.");
            string Get(string name) => records.Current[Array.IndexOf(names, name)].Text;
            var (id, time) = Map(Get("OrderId"), Get("DurationMinutes"), Get("ParametersHash"), Get("FormatVersion"));
            if (!result.TryAdd(id, time)) throw new OrderReadException("Повторяющиеся коды в таблице времени резки.");
        }
        return result;
    }
}
