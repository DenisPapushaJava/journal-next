using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Access;

public sealed class MdbToolsOrderReader(string? executable = null)
{
    public async Task<IReadOnlyList<OrderSummary>> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var export = new MdbToolsTableExport(executable);
        var rows = await export.ReadAsync(path, "Orders", ReadExport, cancellationToken);
        var tables = await export.ListTablesAsync(path, cancellationToken);
        if (tables.Contains(OrderExtrasData.TableName, StringComparer.OrdinalIgnoreCase))
        {
            var times = await export.ReadAsync(path, OrderExtrasData.TableName, OrderExtrasData.ReadExport, cancellationToken);
            rows = rows.Select(row => row with { CuttingTime = times.GetValueOrDefault(row.Id) }).ToArray();
        }
        if (!tables.Contains(OrderAnnotationsData.TableName, StringComparer.OrdinalIgnoreCase)) return rows;
        try
        {
            var annotations = await export.ReadAsync(path, OrderAnnotationsData.TableName, OrderAnnotationsData.ReadExport, cancellationToken);
            return rows.Select(row => row with { Annotations = annotations.GetValueOrDefault(row.Id) }).ToArray();
        }
        catch (Exception exception) when (exception is OrderReadException or IOException or UnauthorizedAccessException)
        {
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.OrderRead, exception);
            return rows.Select(row => row with { AnnotationsUnavailable = true }).ToArray();
        }
    }

    public static IReadOnlyList<OrderSummary> ReadExport(TextReader reader, CancellationToken cancellationToken = default)
    {
        var records = MdbToolsTableExport.ReadCsv(reader, cancellationToken).GetEnumerator();
        using (records)
        {
            if (!records.MoveNext()) throw new OrderReadException("MDB Tools не вернул структуру Orders. Проверьте файл базы.");
            var columns = records.Current.Select(value => value.Text).ToArray();
            AccessOrderMapper.ValidateColumns(columns);
            if (columns.Distinct(StringComparer.Ordinal).Count() != columns.Length)
                throw new OrderReadException("MDB Tools вернул повторяющиеся имена полей Orders.");
            var orders = new List<OrderSummary>();
            while (records.MoveNext())
            {
                var row = records.Current;
                if (row.Count != columns.Length) throw new OrderReadException("Нарушена структура строки Orders; список не заменён.");
                var values = columns.Select((name, index) => (name, field: row[index]))
                    .ToDictionary(item => item.name, item => item.field is { Quoted: false, Text: "NULL" } ? null : (object?)item.field.Text,
                        StringComparer.Ordinal);
                orders.Add(AccessOrderMapper.Map(values));
            }
            return orders;
        }
    }

}
