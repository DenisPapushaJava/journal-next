using System.Data;
using System.Data.OleDb;
using System.Runtime.Versioning;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Infrastructure.Access;

[SupportedOSPlatform("windows")]
public sealed partial class OleDbMachineCatalog(string path) : IMachineCatalogReader, IMachineCreator, IMachineImportEditor
{
    public Task<IReadOnlyList<CuttingMachine>> ReadMachinesAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<CuttingMachine>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = OleDbAccessConnection.Create(path, writable: false); connection.Open();
        return Read(connection, null, cancellationToken);
    }, cancellationToken);

    internal static bool Exists(OleDbConnection connection, string name)
    {
        using var tables = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Tables, [null, null, name, "TABLE"]);
        return tables is not null ? tables.Rows.Count > 0 : throw new PriceReadException("Не удалось проверить каталог станков.");
    }
    internal static CuttingMachine[] Read(OleDbConnection connection, OleDbTransaction? transaction, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Exists(connection, CuttingMachine.CatalogTable)) return [];
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 10;
        command.CommandText = "SELECT * FROM [JournalMachines] ORDER BY [PriceTableName]";
        using var reader = command.ExecuteReader()!;
        if (reader.FieldCount != 3 || !Enumerable.Range(0, 3).Select(reader.GetName).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(["Name", "PriceTableName", "SpecificationProfileId"]))
            throw new PriceReadException("Структура каталога станков несовместима. База не изменена.");
        using var schema = reader.GetSchemaTable();
        if (schema is null || schema.Rows.Cast<DataRow>().Any(field => field["DataType"] is not Type type || type != typeof(string) ||
            Convert.ToInt32(field["ColumnSize"]) != (string.Equals((string)field["ColumnName"], "Name", StringComparison.OrdinalIgnoreCase) ? 255 : 64)))
            throw new PriceReadException("Типы полей каталога станков несовместимы. База не изменена.");
        var machines = new List<CuttingMachine>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (machines.Count >= 128) throw new PriceReadException("Каталог станков слишком большой.");
            if (reader["Name"] is not string || reader["PriceTableName"] is not string)
                throw new PriceReadException("Каталог станков содержит пустое название или прайс.");
            machines.Add(new((string)reader["Name"], (string)reader["PriceTableName"], reader["SpecificationProfileId"] is DBNull ? null : (string)reader["SpecificationProfileId"]));
        }
        try { return CuttingMachine.Snapshot(machines); }
        catch (ArgumentException exception) { throw new PriceReadException("Каталог станков некорректен.", exception); }
    }
    internal static CuttingMachine? Verify(OleDbConnection connection, OleDbTransaction? transaction, string name, CuttingMachine? declared, CancellationToken token) =>
        CuttingMachine.Verify(name, declared, CuttingMachine.LegacyNames.Contains(name) ? [] : Read(connection, transaction, token));
    internal static void RequireRegistered(OleDbConnection connection, OleDbTransaction? transaction, string tableName, CancellationToken token)
    {
        if (CuttingMachine.IsCustomTable(tableName) && !Read(connection, transaction, token).Any(machine => machine.TableName == tableName))
            throw new PriceReadException("Прайс отсутствует в каталоге станков.");
    }

    public Task<CuttingMachine> CreateMachineAsync(MachineCreation creation, CancellationToken cancellationToken = default)
    {
        var snapshot = creation.Snapshot();
        return Task.Run(() => Create(snapshot, cancellationToken), cancellationToken);
    }
    private CuttingMachine Create(MachineCreation creation, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        OleDbConnection? connection = null;
        OleDbTransaction? transaction = null; var commitStarted = false; var ddlStarted = false;
        CuttingMachine[]? before = null; bool catalogExisted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            connection = DiagnosticStages.Run(DiagnosticStage.OpenDatabase, () => OleDbAccessConnection.Create(path, writable: true, exclusive: true));
            if (!File.Exists(path) || !Path.GetExtension(path).Equals(".mdb", StringComparison.OrdinalIgnoreCase))
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "Файл MDB недоступен.");
            if (!connection.Provider.Equals("Microsoft.Jet.OLEDB.4.0", StringComparison.OrdinalIgnoreCase))
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "Создание станка доступно через поставляемый модуль Jet x86.");
            DiagnosticStages.Run(DiagnosticStage.OpenDatabase, () => AccessPriceBackup.Create(path, token));
            token.ThrowIfCancellationRequested(); DiagnosticStages.Run(DiagnosticStage.OpenDatabase, connection.Open);
            catalogExisted = Exists(connection, CuttingMachine.CatalogTable); before = Read(connection, null, token);
            if (!Same(before, creation.ExpectedMachines) || Exists(connection, creation.Machine.TableName)) throw MachineCreation.Conflict();
            transaction = DiagnosticStages.Run(DiagnosticStage.BeginTransaction, () => connection.BeginTransaction(IsolationLevel.ReadCommitted));
            void Execute(string sql)
            {
                token.ThrowIfCancellationRequested(); using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandTimeout = 10; command.CommandText = sql; ddlStarted = true; DiagnosticStages.Run(DiagnosticStage.WriteRecord, command.ExecuteNonQuery);
            }
            if (!catalogExisted)
                Execute("CREATE TABLE [JournalMachines] ([Name] TEXT(255) NOT NULL, [PriceTableName] TEXT(64) NOT NULL PRIMARY KEY, " +
                    "[SpecificationProfileId] TEXT(64), CONSTRAINT [UX_JournalMachineName] UNIQUE ([Name]))");
            Execute($"CREATE TABLE [{creation.Machine.TableName}] ([Код] COUNTER PRIMARY KEY, " +
                string.Join(", ", creation.Rates.Select(rate => $"[{rate.ColumnName}] LONG")) + ")");
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction; insert.CommandTimeout = 10;
                insert.CommandText = "INSERT INTO [JournalMachines] ([Name], [PriceTableName], [SpecificationProfileId]) VALUES (?, ?, ?)";
                insert.Parameters.Add("", OleDbType.VarWChar, 255).Value = creation.Machine.Name;
                insert.Parameters.Add("", OleDbType.VarWChar, 64).Value = creation.Machine.TableName;
                insert.Parameters.Add("", OleDbType.VarWChar, 64).Value = (object?)creation.Machine.SpecificationProfileId ?? DBNull.Value;
                if (DiagnosticStages.Run(DiagnosticStage.WriteRecord, insert.ExecuteNonQuery) != 1) throw new InvalidOperationException("Каталог не подтвердил новый станок.");
                insert.Parameters.Clear();
                insert.CommandText = $"INSERT INTO [{creation.Machine.TableName}] (" + string.Join(", ", creation.Rates.Select(rate => $"[{rate.ColumnName}]")) +
                    ") VALUES (" + string.Join(", ", creation.Rates.Select(_ => "?")) + ")";
                foreach (var rate in creation.Rates) insert.Parameters.Add("", OleDbType.Integer).Value = rate.RublesPerMeter!.Value;
                if (DiagnosticStages.Run(DiagnosticStage.WriteRecord, insert.ExecuteNonQuery) != 1) throw new InvalidOperationException("Прайс не подтвердил тарифы.");
            }
            var actual = DiagnosticStages.Run(DiagnosticStage.ReadBack, () => OleDbPriceReader.Read(connection, transaction, creation.Machine.TableName, token));
            if (!PriceTableChange.Matches(actual, new(creation.Machine.TableName, true, creation.Rates)) ||
                !Same(Read(connection, transaction, token), before.Append(creation.Machine)))
                throw new InvalidOperationException("Сохранённый станок отличается от запроса.");
            token.ThrowIfCancellationRequested(); commitStarted = true; DiagnosticStages.Run(DiagnosticStage.CommitTransaction, transaction.Commit); return creation.Machine;
        }
        catch (Exception exception)
        {
            var uncertain = commitStarted;
            if (transaction is not null)
            {
                try { DiagnosticStages.Run(DiagnosticStage.RollbackTransaction, transaction.Rollback); }
                catch (Exception) { uncertain = true; }
                if (ddlStarted && !uncertain)
                    try
                    {
                        if (Exists(connection!, creation.Machine.TableName) || Exists(connection!, CuttingMachine.CatalogTable) != catalogExisted ||
                            !Same(Read(connection!, null, CancellationToken.None), before!)) uncertain = true;
                    }
                    catch (Exception) { uncertain = true; }
            }
            if (!uncertain && exception is OrderWriteException known) throw known;
            throw new OrderWriteException(uncertain ? OrderWriteFailure.OutcomeUnknown : OrderWriteFailure.NotSaved,
                uncertain ? "Сохранение станка не подтверждено. Обновите каталог перед дальнейшими действиями." :
                    "Станок не добавлен. Закройте подключения на остальных ПК и проверьте доступ к MDB и её папке. Ввод сохранён.", exception);
        }
        finally
        {
            try { DiagnosticStages.Run(DiagnosticStage.ReleaseResources, () => transaction?.Dispose()); } catch (Exception) { }
            try { DiagnosticStages.Run(DiagnosticStage.ReleaseResources, () => connection?.Dispose()); } catch (Exception) { }
        }
    }
    internal static bool Same(IEnumerable<CuttingMachine> left, IEnumerable<CuttingMachine> right) =>
        left.OrderBy(machine => machine.TableName, StringComparer.Ordinal).SequenceEqual(right.OrderBy(machine => machine.TableName, StringComparer.Ordinal));
}
