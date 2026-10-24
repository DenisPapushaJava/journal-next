using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.Runtime.Versioning;
using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Access;

[SupportedOSPlatform("windows")]
public sealed class OleDbOrderAnnotations(string path) : IOrderAnnotationsInitializer
{
    public Task PrepareAnnotationsAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var probe = OleDbAccessConnection.Create(path, writable: false))
            { probe.Open(); if (Exists(probe)) { Validate(probe, null); return; } ValidateOrders(probe); }
            Backup(cancellationToken);
            using var connection = OleDbAccessConnection.Create(path, writable: true, exclusive: true); connection.Open();
            if (Exists(connection)) { Validate(connection, null); return; }
            ValidateOrders(connection); cancellationToken.ThrowIfCancellationRequested();
            using var create = Command(connection, null);
            create.CommandText = $"CREATE TABLE [{OrderAnnotationsData.TableName}] ([OrderId] LONG NOT NULL, [HasBending] BIT NOT NULL, " +
                "[Comment] TEXT(255), [IdentityHash] TEXT(64) NOT NULL, [FormatVersion] SHORT NOT NULL, CONSTRAINT [PK_JournalOrderAnnotations] PRIMARY KEY ([OrderId]))";
            create.ExecuteNonQuery(); Validate(connection, null);
        }
        catch (Exception exception) when (exception is OleDbException or IOException or UnauthorizedAccessException or OrderReadException or InvalidOperationException)
        {
            try { using var check = OleDbAccessConnection.Create(path, writable: false); check.Open(); if (Exists(check)) { Validate(check, null); return; } }
            catch (Exception check) when (check is OleDbException or IOException or UnauthorizedAccessException or OrderReadException or InvalidOperationException)
            { /* Чужую структуру не исправляем. */ }
            throw new OrderWriteException(OrderWriteFailure.NotSaved,
                "Не удалось подготовить гибку и комментарии. Закройте программы на остальных ПК, проверьте права на базу и её папку, затем обновите список (F5). Обычный журнал остаётся доступен.", exception);
        }
    }, cancellationToken);
    private static void ValidateOrders(OleDbConnection connection)
    {
        using var command = Command(connection, null); command.CommandText = "SELECT * FROM [Orders] WHERE 1=0";
        using var reader = command.ExecuteReader()!;
        if (reader.FieldCount != AccessOrderMapper.Columns.Count) throw new OrderReadException("Нужна исходная Orders из 16 полей.");
        AccessOrderMapper.ValidateColumns(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
    }
    private void Backup(CancellationToken token)
    {
        var backup = Path.GetFullPath(path) + ".before-journal-annotations-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".bak";
        var partial = backup + ".partial";
        try
        {
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { source.CopyTo(target); target.Flush(flushToDisk: true); }
            token.ThrowIfCancellationRequested(); File.Move(partial, backup);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }
    internal static bool Exists(OleDbConnection connection) => connection.GetSchema("Tables", [null, null, OrderAnnotationsData.TableName, "TABLE"]).Rows.Count > 0;
    private static void Validate(OleDbConnection connection, OleDbTransaction? transaction)
    {
        using var command = Command(connection, transaction); command.CommandText = $"SELECT * FROM [{OrderAnnotationsData.TableName}] WHERE 1=0";
        using var reader = command.ExecuteReader()!;
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        Type[] types = [typeof(int), typeof(bool), typeof(string), typeof(string), typeof(short)];
        if (names.Length != OrderAnnotationsData.Columns.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(OrderAnnotationsData.Columns) ||
            OrderAnnotationsData.Columns.Where((name, index) => reader.GetFieldType(reader.GetOrdinal(name)) != types[index]).Any())
            throw new OrderReadException("Таблица пометок имеет неизвестную структуру. Она не изменена.");
        reader.Close();
        var primary = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Primary_Keys, [null, null, OrderAnnotationsData.TableName]);
        var columns = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, [null, null, OrderAnnotationsData.TableName, null]);
        if (primary is null || primary.Rows.Count != 1 || (string)primary.Rows[0]["COLUMN_NAME"] != "OrderId") throw new OrderReadException("Неизвестный ключ таблицы пометок.");
        foreach (var (name, size) in new[] { ("Comment", 255), ("IdentityHash", 64) })
        {
            var column = columns?.Rows.Cast<DataRow>().SingleOrDefault(row => (string)row["COLUMN_NAME"] == name);
            if (column is null || Convert.ToInt32(column["CHARACTER_MAXIMUM_LENGTH"], CultureInfo.InvariantCulture) != size) throw new OrderReadException("Неизвестная длина поля пометок.");
        }
    }
    internal static IReadOnlyDictionary<int, OrderAnnotations> ReadAll(OleDbConnection connection, OleDbTransaction? transaction = null, DateOnly? date = null)
    {
        if (!Exists(connection)) return new Dictionary<int, OrderAnnotations>();
        Validate(connection, transaction);
        using var command = Command(connection, transaction);
        command.CommandText = date is null ? $"SELECT * FROM [{OrderAnnotationsData.TableName}]" :
            $"SELECT [e].* FROM [{OrderAnnotationsData.TableName}] AS [e] INNER JOIN [Orders] AS [o] ON [e].[OrderId]=[o].[Код]" + OrderDayQuery.Where(command, date, "[o].[Дата]");
        using var reader = command.ExecuteReader()!; var result = new Dictionary<int, OrderAnnotations>();
        while (reader.Read())
        {
            var (id, value) = Map(reader);
            if (!result.TryAdd(id, value)) throw new OrderReadException("Повторяющийся код пометок.");
        }
        return result;
    }
    internal static OrderAnnotations? ReadOne(OleDbConnection connection, OleDbTransaction? transaction, int id)
    {
        if (!Exists(connection)) return null;
        Validate(connection, transaction);
        using var command = Command(connection, transaction); command.CommandText = $"SELECT * FROM [{OrderAnnotationsData.TableName}] WHERE [OrderId]=?";
        Add(command, OleDbType.Integer, id);
        using var reader = command.ExecuteReader()!;
        if (!reader.Read()) return null;
        var (_, value) = Map(reader); if (reader.Read()) throw new OrderReadException("Повторяющийся код пометок."); return value;
    }
    private static (int, OrderAnnotations) Map(OleDbDataReader reader) => OrderAnnotationsData.Map(reader["OrderId"], reader["HasBending"], reader["Comment"], reader["IdentityHash"], reader["FormatVersion"]);
    internal static void Write(OleDbConnection connection, OleDbTransaction transaction, int id, OrderAnnotations? expected, OrderAnnotations? value)
    {
        value?.Validate();
        if (!Exists(connection))
        {
            if (expected is null && value is null) return;
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Хранение пометок ещё не подготовлено. Обновите список (F5).");
        }
        if (ReadOne(connection, transaction, id) != expected) throw OrderWriteException.Conflict();
        if (value == expected) return;
        using var command = Command(connection, transaction);
        if (value is null) command.CommandText = $"DELETE FROM [{OrderAnnotationsData.TableName}]";
        else
        {
            command.CommandText = expected is null
                ? $"INSERT INTO [{OrderAnnotationsData.TableName}] ([HasBending],[Comment],[IdentityHash],[FormatVersion],[OrderId]) VALUES (?,?,?,1,?)"
                : $"UPDATE [{OrderAnnotationsData.TableName}] SET [HasBending]=?,[Comment]=?,[IdentityHash]=?,[FormatVersion]=1";
            Add(command, OleDbType.Boolean, value.HasBending); Add(command, OleDbType.VarWChar, value.Comment.Length == 0 ? null : value.Comment);
            Add(command, OleDbType.VarWChar, value.IdentityHash, 64);
        }
        if (expected is null) Add(command, OleDbType.Integer, id);
        else
        {
            command.CommandText += " WHERE [OrderId]=? AND [HasBending]=? AND StrComp([IdentityHash],?,0)=0 AND [FormatVersion]=1 AND " + (expected.Comment.Length == 0 ? "([Comment] IS NULL OR StrComp([Comment],?,0)=0)" : "StrComp([Comment],?,0)=0");
            Add(command, OleDbType.Integer, id); Add(command, OleDbType.Boolean, expected.HasBending); Add(command, OleDbType.VarWChar, expected.IdentityHash, 64);
            Add(command, OleDbType.VarWChar, expected.Comment);
        }
        if (command.ExecuteNonQuery() != 1) throw OrderWriteException.Conflict();
    }
    private static OleDbCommand Command(OleDbConnection connection, OleDbTransaction? transaction)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 10; return command; }
    private static void Add(OleDbCommand command, OleDbType type, object? value, int size = 255)
    { var parameter = command.Parameters.Add("p" + command.Parameters.Count, type); if (type == OleDbType.VarWChar) parameter.Size = size; parameter.Value = value ?? DBNull.Value; }
}
