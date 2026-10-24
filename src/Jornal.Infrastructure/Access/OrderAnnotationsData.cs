using System.Globalization;
using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Access;

public static class OrderAnnotationsData
{
    public const string TableName = "JournalOrderAnnotations";
    public static readonly string[] Columns = ["OrderId", "HasBending", "Comment", "IdentityHash", "FormatVersion"];
    public static (int Id, OrderAnnotations Value) Map(object id, object bending, object? comment, object hash, object version)
    {
        try
        {
            var number = Convert.ToDecimal(id, CultureInfo.InvariantCulture);
            if (number <= 0 || number != decimal.Truncate(number) || Convert.ToDecimal(version, CultureInfo.InvariantCulture) != 1) throw new FormatException();
            var flag = bending is bool boolean ? boolean : Convert.ToString(bending, CultureInfo.InvariantCulture)?.ToLowerInvariant() switch
            { "true" or "1" or "-1" => true, "false" or "0" => false, _ => throw new FormatException() };
            var value = new OrderAnnotations(flag, comment is null or DBNull ? "" : (string)comment, (string)hash);
            value.Validate(); return (checked((int)number), value);
        }
        catch (Exception exception) when (exception is InvalidCastException or FormatException or OverflowException or OrderWriteException)
        { throw new OrderReadException("Не удалось прочитать гибку и комментарий. Неизвестная пометка не заменена пустой.", exception); }
    }
    public static IReadOnlyDictionary<int, OrderAnnotations> ReadExport(TextReader reader, CancellationToken token = default)
    {
        using var rows = MdbToolsTableExport.ReadCsv(reader, token, tableName: TableName).GetEnumerator();
        if (!rows.MoveNext()) throw new OrderReadException("Не прочитана структура пометок.");
        var names = rows.Current.Select(value => value.Text).ToArray();
        if (names.Length != Columns.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(Columns))
            throw new OrderReadException("Неизвестная структура таблицы пометок.");
        var result = new Dictionary<int, OrderAnnotations>();
        while (rows.MoveNext())
        {
            if (rows.Current.Count != Columns.Length) throw new OrderReadException("Повреждена строка пометок.");
            MdbToolsTableExport.CsvField Field(string name) => rows.Current[Array.IndexOf(names, name)];
            // CSV различает пустой текст/буквальное NULL и SQL NULL.
            var text = Field("Comment");
            var (id, value) = Map(Field("OrderId").Text, Field("HasBending").Text,
                text is { Quoted: false, Text: "NULL" } ? null : text.Text, Field("IdentityHash").Text, Field("FormatVersion").Text);
            if (!result.TryAdd(id, value)) throw new OrderReadException("Повторяющиеся коды в таблице пометок.");
        }
        return result;
    }
}
