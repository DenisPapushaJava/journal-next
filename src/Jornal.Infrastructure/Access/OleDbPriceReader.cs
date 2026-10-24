using Jornal.Infrastructure.Diagnostics;
using System.Data;
using System.Data.OleDb;
using System.Runtime.Versioning;
using Jornal.Core.Pricing;

namespace Jornal.Infrastructure.Access;

[SupportedOSPlatform("windows")]
public static class OleDbPriceReader
{
    public static Task<PriceTable> ReadAsync(string path, string tableName, CancellationToken cancellationToken = default)
    {
        PriceTables.ValidateName(tableName);
        return Task.Run(() => Read(path, tableName, cancellationToken), cancellationToken);
    }

    private static PriceTable Read(string path, string tableName, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = DiagnosticStages.Run(DiagnosticStage.OpenDatabase, () => OleDbAccessConnection.Create(path, writable: false));
            DiagnosticStages.Run(DiagnosticStage.OpenDatabase, connection.Open);
            cancellationToken.ThrowIfCancellationRequested();
            return Read(connection, null, tableName, cancellationToken);
        }
        catch (Exception exception) when (exception is OleDbException or InvalidOperationException)
        {
            throw new PriceReadException(DataSourceAccessErrors.Describe(path, exception,
                "Не удалось прочитать первую строку прайса " + tableName + ". Проверьте наличие таблицы, доступ к MDB и драйвер."), exception);
        }
    }

    internal static PriceTable Read(OleDbConnection connection, OleDbTransaction? transaction, string tableName, CancellationToken cancellationToken) => DiagnosticStages.Run(DiagnosticStage.ReadPrices, () =>
    {
        PriceTables.ValidateName(tableName);
        OleDbMachineCatalog.RequireRegistered(connection, transaction, tableName, cancellationToken);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Только имя из фиксированного списка. Никаких имён таблиц/SQL из полей формы.
        command.CommandText = $"SELECT TOP 1 * FROM [{tableName}]";
        command.CommandTimeout = 30;
        using var reader = command.ExecuteReader(CommandBehavior.SingleResult)!;
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var first = reader.Read() ? Enumerable.Range(0, reader.FieldCount).Select(index => (object?)reader.GetValue(index)).ToArray() : null;
        cancellationToken.ThrowIfCancellationRequested();
        return AccessPriceMapper.Map(tableName, columns, first);
    });
}
