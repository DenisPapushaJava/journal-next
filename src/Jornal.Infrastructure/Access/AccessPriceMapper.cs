using System.Globalization;
using Jornal.Core.Pricing;

namespace Jornal.Infrastructure.Access;

public static class AccessPriceMapper
{
    public static PriceTable Map(string tableName, IReadOnlyList<string> columns, IReadOnlyList<object?>? firstRow)
    {
        PriceTables.ValidateName(tableName);
        if (columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count ||
            (firstRow is not null && firstRow.Count != columns.Count))
            throw new PriceReadException("Нарушена структура таблицы прайса.");
        var rates = new List<PriceRate>();
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            if (!decimal.TryParse(column.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var thickness) || thickness <= 0) continue;
            int? amount = null;
            if (firstRow?[index] is { } value && value != DBNull.Value)
            {
                var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                    throw new PriceReadException("Тариф прайса не является целым числом Long Integer: " + tableName + ", " + column + ".");
                amount = parsed;
            }
            rates.Add(new(thickness, column, amount));
        }
        var table = new PriceTable(tableName, firstRow is not null, rates.ToArray());
        try { table.Validate(); }
        catch (ArgumentException exception) { throw new PriceReadException("Неоднозначные колонки толщины в прайсе " + tableName + ".", exception); }
        return table;
    }

    public static PriceTable ReadExport(string tableName, TextReader reader, CancellationToken cancellationToken = default, char delimiter = ',')
    {
        using var records = MdbToolsTableExport.ReadCsv(reader, cancellationToken, delimiter, tableName).GetEnumerator();
        if (!records.MoveNext()) throw new PriceReadException("MDB Tools не вернул структуру прайса " + tableName + ".");
        var columns = records.Current.Select(field => field.Text).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        // Как в старом приложении, используется только первая строка, а не последний тариф или сумма строк.
        var firstRow = records.MoveNext() ? records.Current.Select(field =>
            field is { Quoted: false, Text: "NULL" } ? null : (object?)field.Text).ToArray() : null;
        return Map(tableName, columns, firstRow);
    }
}
