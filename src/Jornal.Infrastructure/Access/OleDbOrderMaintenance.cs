using Jornal.Infrastructure.Diagnostics;
using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using System.Runtime.Versioning;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;

namespace Jornal.Infrastructure.Access;

[SupportedOSPlatform("windows")]
public sealed class OleDbOrderMaintenance(string path) : IOrderMaintenance, IMachineCatalogReader
{
    public Task<IReadOnlyList<CuttingMachine>> ReadMachinesAsync(CancellationToken cancellationToken = default) => new OleDbMachineCatalog(path).ReadMachinesAsync(cancellationToken);
    private static readonly string[] Names = AccessOrderMapper.Columns.ToArray();
    private static readonly OleDbType[] Types = [OleDbType.Integer, OleDbType.Date, OleDbType.VarWChar, OleDbType.VarWChar,
        OleDbType.VarWChar, OleDbType.VarWChar, OleDbType.VarWChar, OleDbType.VarWChar, OleDbType.VarWChar,
        OleDbType.Currency, OleDbType.Double, OleDbType.Integer, OleDbType.VarWChar, OleDbType.VarWChar, OleDbType.Integer, OleDbType.VarWChar];

    public Task<PriceTable> ReadAsync(string tableName, CancellationToken cancellationToken = default) =>
        OleDbPriceReader.ReadAsync(path, tableName, cancellationToken);

    public Task<OrderRecord?> ReadRecordAsync(int id, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        try
        {
            using var connection = DiagnosticStages.Run(DiagnosticStage.OpenDatabase, () => OleDbAccessConnection.Create(path, writable: false));
            DiagnosticStages.Run(DiagnosticStage.OpenDatabase, connection.Open);
            var result = DiagnosticStages.Run(DiagnosticStage.ReadOrders, () => Read(connection, null, id));
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception exception) when (exception is OleDbException or IOException or InvalidOperationException)
        { throw new OrderReadException(DataSourceAccessErrors.Describe(path, exception, "Не удалось прочитать полную заявку из MDB. Проверьте доступ к базе."), exception); }
    }, cancellationToken);

    public Task<OrderRecord> UpdateAsync(OrderEditChange change, CancellationToken cancellationToken = default)
    {
        change.Validate();
        return PrepareAndWrite(change.Values.CuttingTime is not null, change.Values.Annotations is not null, (connection, transaction, token) =>
        {
            if (DiagnosticStages.Run(DiagnosticStage.CheckSnapshot, () => Read(connection, transaction, change.Expected.Id)) != change.Expected) throw Conflict();
            var machine = OleDbMachineCatalog.Verify(connection, transaction, change.PriceRequest.CuttingType, change.Machine, token);
            var table = change.RequiresPriceCalculation ? OleDbPriceReader.Read(connection, transaction, CuttingPriceCalculator.SelectTable(change.PriceRequest, machine), token) : null;
            var values = DiagnosticStages.Run(DiagnosticStage.CalculatePrice, () => change.Calculate(table, machine));
            using var command = Command(connection, transaction);
            command.CommandText = "UPDATE [Orders] SET " + string.Join(", ", Names.Skip(1).Select(name => $"[{name}] = ?"));
            AddValues(command, values, skipId: true);
            command.CommandText += Where(command, change.Expected);
            if (DiagnosticStages.Run(DiagnosticStage.WriteRecord, command.ExecuteNonQuery) != 1) throw Conflict();
            DiagnosticStages.Run(DiagnosticStage.WriteRecord, () => OleDbOrderExtras.Write(connection, transaction, values.Id, change.Expected.CuttingTime, values.CuttingTime));
            DiagnosticStages.Run(DiagnosticStage.WriteRecord, () => OleDbOrderAnnotations.Write(connection, transaction, values.Id, change.Expected.Annotations, values.Annotations));
            var saved = DiagnosticStages.Run(DiagnosticStage.ReadBack, () => Read(connection, transaction, values.Id));
            if (saved != values) throw new InvalidOperationException("MDB изменила сохраняемые поля.");
            return saved!;
        }, cancellationToken);
    }

    public async Task DeleteAsync(OrderRecord expected, CancellationToken cancellationToken = default)
    {
        expected.Validate();
        await Write((connection, transaction, token) =>
        {
            // Проверяем полный контракт до DELETE, включая отсутствие дополнительных полей.
            if (DiagnosticStages.Run(DiagnosticStage.CheckSnapshot, () => Read(connection, transaction, expected.Id)) != expected) throw Conflict();
            using var relations = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Foreign_Keys, null);
            if (relations is null || relations.Rows.Cast<DataRow>().Any(row => string.Equals(row["PK_TABLE_NAME"] as string, "Orders", StringComparison.OrdinalIgnoreCase)))
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "У Orders есть зависимые таблицы либо связи не удалось проверить. Удаление остановлено: архив одной строки не сохраняет связанные записи.");
            using var command = Command(connection, transaction);
            command.CommandText = "DELETE FROM [Orders]" + Where(command, expected);
            if (DiagnosticStages.Run(DiagnosticStage.WriteRecord, command.ExecuteNonQuery) != 1) throw Conflict();
            DiagnosticStages.Run(DiagnosticStage.WriteRecord, () => OleDbOrderExtras.Write(connection, transaction, expected.Id, expected.CuttingTime, null));
            DiagnosticStages.Run(DiagnosticStage.WriteRecord, () => OleDbOrderAnnotations.Write(connection, transaction, expected.Id, expected.Annotations, null));
            return true;
        }, cancellationToken);
    }

    public Task<OrderRecord> RestoreAsync(OrderRecord archived, CancellationToken cancellationToken = default)
    {
        archived.Validate();
        return PrepareAndWrite(archived.CuttingTime is not null, archived.Annotations is not null, (connection, transaction, token) =>
        {
            if (DiagnosticStages.Run(DiagnosticStage.CheckSnapshot, () => Read(connection, transaction, archived.Id)) is not null)
                throw new OrderWriteException(OrderWriteFailure.Conflict, "Исходный код уже занят. Существующая заявка не заменена; копия осталась в корзине.");
            using var command = Command(connection, transaction);
            command.CommandText = "INSERT INTO [Orders] (" + string.Join(", ", Names.Select(name => $"[{name}]")) + ") VALUES (" + string.Join(", ", Names.Select(_ => "?")) + ")";
            AddValues(command, archived);
            if (DiagnosticStages.Run(DiagnosticStage.WriteRecord, command.ExecuteNonQuery) != 1) throw new InvalidOperationException("INSERT не подтвердил восстановление.");
            DiagnosticStages.Run(DiagnosticStage.WriteRecord, () => OleDbOrderExtras.Write(connection, transaction, archived.Id, null, archived.CuttingTime));
            DiagnosticStages.Run(DiagnosticStage.WriteRecord, () => OleDbOrderAnnotations.Write(connection, transaction, archived.Id, null, archived.Annotations));
            var saved = DiagnosticStages.Run(DiagnosticStage.ReadBack, () => Read(connection, transaction, archived.Id));
            if (saved != archived) throw new InvalidOperationException("MDB изменила восстановленные поля.");
            return saved!;
        }, cancellationToken);
    }

    private async Task<T> PrepareAndWrite<T>(bool needsTime, bool needsAnnotations, Func<OleDbConnection, OleDbTransaction, CancellationToken, T> action, CancellationToken token)
    {
        if (needsTime) await DiagnosticStages.RunAsync(DiagnosticStage.PrepareCuttingTime, () => new OleDbOrderExtras(path).PrepareCuttingTimeAsync(token));
        if (needsAnnotations) await new OleDbOrderAnnotations(path).PrepareAnnotationsAsync(token);
        return await Write(action, token);
    }

    private Task<T> Write<T>(Func<OleDbConnection, OleDbTransaction, CancellationToken, T> action, CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        OleDbConnection? connection = null;
        OleDbTransaction? transaction = null;
        var committing = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".mdb", StringComparison.OrdinalIgnoreCase))
                throw new OrderWriteException(OrderWriteFailure.NotSaved, DataSourceAccessErrors.Unavailable(path));
            connection = DiagnosticStages.Run(DiagnosticStage.OpenDatabase, () => OleDbAccessConnection.Create(path, writable: true));
            DiagnosticStages.Run(DiagnosticStage.OpenDatabase, connection.Open);
            transaction = DiagnosticStages.Run(DiagnosticStage.BeginTransaction, () => connection.BeginTransaction(IsolationLevel.ReadCommitted));
            try
            {
                var result = action(connection, transaction, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                committing = true;
                DiagnosticStages.Run(DiagnosticStage.CommitTransaction, transaction.Commit);
                return result;
            }
            catch (Exception exception)
            {
                var rollbackFailed = false;
                try { DiagnosticStages.Run(DiagnosticStage.RollbackTransaction, transaction.Rollback); }
                catch (Exception rollback) { rollbackFailed = true; AppDiagnostics.Capture(DiagnosticOperation.OrderEdit, rollback); }
                if (committing || rollbackFailed)
                    throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown,
                        "Результат записи не подтверждён. Изменение могло примениться. Обновите список (F5) и проверьте запись перед повтором.", exception);
                throw;
            }
        }
        catch (OrderWriteException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is OleDbException or IOException or UnauthorizedAccessException or InvalidOperationException or OrderReadException or OperationCanceledException)
        { throw new OrderWriteException(OrderWriteFailure.NotSaved, DataSourceAccessErrors.Describe(path, exception,
            "Не удалось изменить заявку. Проверьте доступ к MDB и блокировки. Ввод/архив сохранены."), exception); }
        finally { Release(transaction); Release(connection); }
    }, cancellationToken);

    private static void Release(IDisposable? value)
    {
        try { DiagnosticStages.Run(DiagnosticStage.ReleaseResources, () => value?.Dispose()); } catch (Exception exception) { AppDiagnostics.Capture(DiagnosticOperation.OrderEdit, exception); }
    }

    private static OrderWriteException Conflict() => new(OrderWriteFailure.Conflict,
        "Заявка изменена или удалена другим пользователем. Данные не перезаписаны. Обновите список (F5); введённые правки/архив сохранены.");

    private static OleDbCommand Command(OleDbConnection connection, OleDbTransaction? transaction)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 10; return command;
    }

    private static object?[] Values(OrderRecord row) => [row.Id, row.Date, row.Manager, row.Specification, row.Customer, row.CuttingType,
        row.Material, row.SheetSize, row.SheetCount, row.Price, row.Thickness, row.CutLength, row.Weight, row.DeliveryNumber, row.Color, row.Coefficient];

    private static void Add(OleDbCommand command, int column, object? value)
    {
        var parameter = command.Parameters.Add("p" + command.Parameters.Count, Types[column]);
        if (Types[column] == OleDbType.VarWChar) parameter.Size = 255;
        parameter.Value = value ?? DBNull.Value;
    }

    private static void AddValues(OleDbCommand command, OrderRecord row, bool skipId = false)
    {
        var values = Values(row);
        for (var index = skipId ? 1 : 0; index < values.Length; index++) Add(command, index, values[index]);
    }

    private static string Where(OleDbCommand command, OrderRecord expected)
    {
        var values = Values(expected);
        var conditions = new List<string>();
        for (var index = 0; index < Names.Length; index++)
        {
            conditions.Add(values[index] is null ? $"[{Names[index]}] IS NULL" : Types[index] == OleDbType.VarWChar
                ? $"StrComp([{Names[index]}], ?, 0) = 0" : $"[{Names[index]}] = ?");
            if (values[index] is not null) Add(command, index, values[index]);
        }
        return " WHERE " + string.Join(" AND ", conditions);
    }

    private static OrderRecord? Read(OleDbConnection connection, OleDbTransaction? transaction, int id)
    {
        using var command = Command(connection, transaction);
        command.CommandText = "SELECT * FROM [Orders] WHERE [Код] = ?";
        Add(command, 0, id);
        using var reader = command.ExecuteReader(CommandBehavior.SingleResult)!;
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        if (names.Length != Names.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(Names))
            throw new OrderReadException("Для полной записи нужна исходная схема Orders из 16 полей. Структура базы не изменена.");
        object? Get(string name) { var value = reader[name]; return value is DBNull ? null : value; }
        // Проверяем типы даже пустой таблицы до будущего INSERT.
        var expectedTypes = new[] { typeof(int), typeof(DateTime), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
            typeof(string), typeof(decimal), typeof(double), typeof(int), typeof(string), typeof(string), typeof(int), typeof(string) };
        for (var i = 0; i < Names.Length; i++)
            if (reader.GetFieldType(reader.GetOrdinal(Names[i])) != expectedTypes[i])
                throw new OrderReadException($"Тип поля «{Names[i]}» отличается от прежней MDB. Изменения не выполнены.");
        if (!reader.Read()) return null;
        var row = new OrderRecord((int)Get("Код")!, (DateTime?)Get("Дата"), (string?)Get("Менеджер"), (string?)Get("Спецификация"),
            (string?)Get("Заказчик"), (string?)Get("Вид_резки"), (string?)Get("Марка_стали"), (string?)Get("Размер_листа"),
            (string?)Get("Кол_во_листов"), (decimal?)Get("Стоимость"), (double?)Get("Толщина"), (int?)Get("Ход"), (string?)Get("Вес"),
            (string?)Get("БЛЦБ"), (int?)Get("Color"), (string?)Get("Коэффициент"));
        reader.Close();
        row = row with { CuttingTime = OleDbOrderExtras.ReadOne(connection, transaction, id), Annotations = OleDbOrderAnnotations.ReadOne(connection, transaction, id) };
        row.Validate(); return row;
    }
}
