using System.Data;
using System.Data.OleDb;
using System.Runtime.Versioning;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Infrastructure.Access;

[SupportedOSPlatform("windows")]
public sealed class OleDbPriceEditor(string path) : IPriceEditor, IMachineCreator, IMachineImportEditor, IPriceImpactReader
{
    public Task<CuttingMachine> UpdateMachineImportAsync(MachineImportChange change, CancellationToken cancellationToken = default) =>
        new OleDbMachineCatalog(path).UpdateMachineImportAsync(change, cancellationToken);
    public Task<IReadOnlyList<PriceThicknessUsage>> ReadThicknessUsageAsync(PriceTableChange change, CancellationToken cancellationToken = default) =>
        new OleDbPriceImpactReader(path).ReadThicknessUsageAsync(change, cancellationToken);
    public Task<CuttingMachine> CreateMachineAsync(MachineCreation creation, CancellationToken cancellationToken = default) =>
        new OleDbMachineCatalog(path).CreateMachineAsync(creation, cancellationToken);
    public Task<PriceTable> SaveAsync(PriceTableChange change, CancellationToken cancellationToken = default)
    {
        var snapshot = change.Snapshot();
        return Task.Run(() => snapshot.HasRenames ? new JetPriceRenamer(path).Save(snapshot, cancellationToken) : Save(snapshot, cancellationToken), cancellationToken);
    }

    private PriceTable Save(PriceTableChange change, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        OleDbConnection? connection = null;
        OleDbTransaction? transaction = null;
        var commitStarted = false;
        var ddlStarted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".mdb", StringComparison.OrdinalIgnoreCase))
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "Файл MDB недоступен.");
            connection = DiagnosticStages.Run(DiagnosticStage.OpenDatabase, () => OleDbAccessConnection.Create(path, writable: true, exclusive: change.HasAdditions));
            if (change.HasAdditions)
            {
                // Для Jet 4 документирован DBPROP_SUPPORTEDTXNDDL = DBPROPVAL_TC_ALL.
                // Не полагаемся на неизвестную транзакционность DDL другого провайдера.
                if (!string.Equals(connection.Provider, "Microsoft.Jet.OLEDB.4.0", StringComparison.OrdinalIgnoreCase))
                    throw new OrderWriteException(OrderWriteFailure.NotSaved,
                        "Добавление толщин в MDB доступно через поставляемый модуль Jet x86. Запустите установленный комплект Journal.");
                DiagnosticStages.Run(DiagnosticStage.OpenDatabase, () => AccessPriceBackup.Create(path, token));
            }
            DiagnosticStages.Run(DiagnosticStage.OpenDatabase, connection.Open);
            OleDbMachineCatalog.RequireRegistered(connection, null, change.Expected.Name, token);
            transaction = DiagnosticStages.Run(DiagnosticStage.BeginTransaction, () => connection.BeginTransaction(IsolationLevel.ReadCommitted));
            try
            {
                var fieldCount = 0;
                var current = DiagnosticStages.Run(DiagnosticStage.ReadPrices, () => ReadSingle(connection, transaction, change.Expected.Name, out fieldCount));
                if (!PriceTableChange.Matches(current, change.Expected)) throw PriceTableChange.Conflict();
                var added = change.AddedRates.ToArray();
                if (fieldCount + added.Length > 255)
                    throw new OrderWriteException(OrderWriteFailure.NotSaved, "В таблице MDB не может быть больше 255 полей.");
                foreach (var rate in added)
                {
                    token.ThrowIfCancellationRequested();
                    using var alter = connection.CreateCommand(); alter.Transaction = transaction; alter.CommandTimeout = 10;
                    alter.CommandText = $"ALTER TABLE {Quote(current.Name)} ADD COLUMN {Quote(rate.ColumnName)} LONG";
                    ddlStarted = true;
                    DiagnosticStages.Run(DiagnosticStage.WriteRecord, alter.ExecuteNonQuery);
                }
                var differences = current.Rates.Zip(change.Values.Rates).Where(pair => pair.First != pair.Second).ToArray();
                if (differences.Length > 0 || added.Length > 0)
                {
                    token.ThrowIfCancellationRequested();
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction; update.CommandTimeout = 10;
                    var assignments = differences.Select(pair => pair.Second).Concat(added).Select(rate =>
                    {
                        Add(update, rate.RublesPerMeter);
                        return Quote(rate.ColumnName) + " = ?";
                    }).ToArray();
                    var conditions = current.Rates.Select(rate =>
                    {
                        if (rate.RublesPerMeter is null) return Quote(rate.ColumnName) + " IS NULL";
                        Add(update, rate.RublesPerMeter); return Quote(rate.ColumnName) + " = ?";
                    }).ToArray();
                    update.CommandText = $"UPDATE {Quote(current.Name)} SET {string.Join(", ", assignments)} WHERE {string.Join(" AND ", conditions)}";
                    if (DiagnosticStages.Run(DiagnosticStage.WriteRecord, update.ExecuteNonQuery) != 1) throw PriceTableChange.Conflict();
                }
                var saved = DiagnosticStages.Run(DiagnosticStage.ReadBack, () => ReadSingle(connection, transaction, current.Name));
                if (!PriceTableChange.Matches(saved, change.Values)) throw PriceTableChange.Conflict();
                token.ThrowIfCancellationRequested(); commitStarted = true;
                DiagnosticStages.Run(DiagnosticStage.CommitTransaction, transaction.Commit);
                return saved;
            }
            catch (Exception exception)
            {
                var rollbackFailed = false;
                try { DiagnosticStages.Run(DiagnosticStage.RollbackTransaction, transaction.Rollback); }
                catch (Exception rollback) { rollbackFailed = true; AppDiagnostics.Capture(DiagnosticOperation.AccessHost, rollback); }
                if (ddlStarted && !commitStarted && !rollbackFailed)
                {
                    try
                    {
                        if (!PriceTableChange.Matches(change.Expected,
                            DiagnosticStages.Run(DiagnosticStage.ReadBack, () => ReadSingle(connection, null, change.Expected.Name)))) rollbackFailed = true;
                    }
                    catch (Exception verification) { rollbackFailed = true; AppDiagnostics.Capture(DiagnosticOperation.AccessHost, verification); }
                }
                if (commitStarted || rollbackFailed)
                    throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown,
                        "Сохранение прайса не подтверждено. Обновите прайс перед повтором; изменения могли примениться.", exception);
                throw;
            }
        }
        catch (OrderWriteException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is OleDbException or IOException or UnauthorizedAccessException or
            InvalidOperationException or OrderReadException or PriceReadException or OperationCanceledException)
        {
            throw new OrderWriteException(OrderWriteFailure.NotSaved,
                change.HasAdditions
                    ? "Толщины не добавлены. Закройте Journal и старую программу на остальных ПК, проверьте доступ к MDB и её папке. Ввод сохранён."
                    : DataSourceAccessErrors.Describe(path, exception,
                        "Прайс не сохранён. Проверьте подключение к базе, ограничения полей и блокировки MDB."), exception);
        }
        finally { Release(transaction); Release(connection); }
    }

    internal static PriceTable ReadSingle(OleDbConnection connection, OleDbTransaction? transaction, string name)
        => ReadSingle(connection, transaction, name, out _);

    private static PriceTable ReadSingle(OleDbConnection connection, OleDbTransaction? transaction, string name, out int fieldCount)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 10;
        command.CommandText = $"SELECT * FROM {Quote(name)}";
        using var reader = command.ExecuteReader(CommandBehavior.SingleResult)!;
        fieldCount = reader.FieldCount;
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var row = reader.Read() ? Enumerable.Range(0, reader.FieldCount).Select(i => (object?)reader.GetValue(i)).ToArray() : null;
        if (row is null || reader.Read())
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Для редактирования прайс должен содержать ровно одну строку. Структура базы не изменена.");
        var table = AccessPriceMapper.Map(name, names, row);
        foreach (var rate in table.Rates)
            if (reader.GetFieldType(Array.IndexOf(names, rate.ColumnName)) != typeof(int))
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "Тип тарифа отличается от Long Integer. Структура базы не изменена.");
        return table;
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
    private static void Add(OleDbCommand command, int? value) =>
        command.Parameters.Add("p" + command.Parameters.Count, OleDbType.Integer).Value = (object?)value ?? DBNull.Value;
    private static void Release(IDisposable? resource)
    {
        try { DiagnosticStages.Run(DiagnosticStage.ReleaseResources, () => resource?.Dispose()); }
        catch (Exception exception) { AppDiagnostics.Capture(DiagnosticOperation.AccessHost, exception); }
    }
}
