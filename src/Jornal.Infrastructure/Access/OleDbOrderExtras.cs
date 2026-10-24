using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.Runtime.Versioning;
using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Access;

// Дополнительная таблица без FK: старый EXE должен сохранять право DELETE из Orders.
[SupportedOSPlatform("windows")]
public sealed class OleDbOrderExtras(string path) : IOrderDurationInitializer
{
    public const string TableName = "JournalOrderExtras";
    private static readonly string[] Names = ["OrderId", "DurationMinutes", "ParametersHash", "FormatVersion"];

    public Task PrepareCuttingTimeAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using (var probe = OleDbAccessConnection.Create(path, writable: false))
            {
                probe.Open();
                if (Exists(probe)) { Validate(probe, null); return; }
                ValidateOrders(probe);
            }
            CreateBackup(cancellationToken);
            // Только однократная подготовка требует исключительного доступа. Рабочие записи — shared.
            using var connection = OleDbAccessConnection.Create(path, writable: true, exclusive: true);
            connection.Open();
            if (Exists(connection)) { Validate(connection, null); return; }
            ValidateOrders(connection);
            cancellationToken.ThrowIfCancellationRequested();
            using (var create = Command(connection, null))
            {
                // Jet не имеет Int64; DECIMAL хранит целые минуты без потери точности.
                create.CommandText = $"CREATE TABLE [{TableName}] ([OrderId] LONG NOT NULL, [DurationMinutes] DECIMAL(18,0) NOT NULL, " +
                    "[ParametersHash] TEXT(64) NOT NULL, [FormatVersion] SHORT NOT NULL, CONSTRAINT [PK_JournalOrderExtras] PRIMARY KEY ([OrderId]))";
                create.ExecuteNonQuery();
            }
            Validate(connection, null);
        }
        catch (Exception exception) when (exception is OleDbException or IOException or UnauthorizedAccessException or OrderReadException or InvalidOperationException)
        {
            // Другой клиент мог уже завершить ту же подготовку. Чужую/неполную таблицу не исправляем.
            try
            {
                using var check = OleDbAccessConnection.Create(path, writable: false); check.Open();
                if (Exists(check)) { Validate(check, null); return; }
            }
            catch (Exception) { /* Ни подтверждения, ни разрешения продолжить запись времени. */ }
            throw new OrderWriteException(OrderWriteFailure.NotSaved,
                "Не удалось подготовить хранение времени резки. Закройте Journal и старую программу на остальных ПК, проверьте права на базу и её папку, затем обновите список (F5). Обычный журнал остаётся доступен.", exception);
        }
    }, cancellationToken);

    private static void ValidateOrders(OleDbConnection connection)
    {
        using var command = Command(connection, null);
        command.CommandText = "SELECT * FROM [Orders] WHERE 1=0";
        using var reader = command.ExecuteReader()!;
        if (reader.FieldCount != AccessOrderMapper.Columns.Count)
            throw new OrderReadException("Для подготовки времени нужна исходная таблица Orders из 16 полей.");
        AccessOrderMapper.ValidateColumns(Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
    }

    private void CreateBackup(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var backup = Path.GetFullPath(path) + ".before-journal-extras-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) +
            "-" + Guid.NewGuid().ToString("N") + ".bak";
        var partial = backup + ".partial";
        try
        {
            // Jet Share Exclusive запрещает чтение вторым дескриптором, даже в том же процессе.
            // Согласованную копию создаём до подключения Jet, запретив любой доступ к исходному файлу.
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, backup);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
        // Между закрытием файла и Open Jet может подключиться другой клиент.
        // Поэтому DDL допускается только после повторного захвата исключительного доступа и проверки схемы.
    }

    internal static bool Exists(OleDbConnection connection) => connection.GetSchema("Tables", [null, null, TableName, "TABLE"]).Rows.Count > 0;

    private static void Validate(OleDbConnection connection, OleDbTransaction? transaction)
    {
        using var command = Command(connection, transaction); command.CommandText = $"SELECT * FROM [{TableName}] WHERE 1=0";
        using var reader = command.ExecuteReader()!;
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var types = new[] { typeof(int), typeof(decimal), typeof(string), typeof(short) };
        if (names.Length != Names.Length || !names.ToHashSet(StringComparer.Ordinal).SetEquals(Names) ||
            Names.Where((name, i) => reader.GetFieldType(reader.GetOrdinal(name)) != types[i]).Any())
            throw new OrderReadException("Таблица JournalOrderExtras имеет неизвестную структуру. Она не изменена.");
        reader.Close();
        var columns = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Columns, [null, null, TableName, null]);
        var primary = connection.GetOleDbSchemaTable(OleDbSchemaGuid.Primary_Keys, [null, null, TableName]);
        if (primary is null || primary.Rows.Count != 1 || (string)primary.Rows[0]["COLUMN_NAME"] != "OrderId")
            throw new OrderReadException("У таблицы времени нет ожидаемого первичного ключа.");
        var hash = columns?.Rows.Cast<DataRow>().SingleOrDefault(r => string.Equals(r["COLUMN_NAME"] as string, "ParametersHash", StringComparison.Ordinal));
        if (hash is null || Convert.ToInt32(hash["CHARACTER_MAXIMUM_LENGTH"], CultureInfo.InvariantCulture) != 64)
            throw new OrderReadException("Неподдерживаемая длина поля привязки времени резки.");
    }

    internal static IReadOnlyDictionary<int, OrderCuttingTime> ReadAll(OleDbConnection connection, OleDbTransaction? transaction = null, DateOnly? date = null)
    {
        if (!Exists(connection)) return new Dictionary<int, OrderCuttingTime>();
        Validate(connection, transaction);
        using var command = Command(connection, transaction);
        command.CommandText = date is null ? $"SELECT * FROM [{TableName}]" :
            $"SELECT [e].* FROM [{TableName}] AS [e] INNER JOIN [Orders] AS [o] ON [e].[OrderId]=[o].[Код]" + OrderDayQuery.Where(command, date, "[o].[Дата]");
        using var reader = command.ExecuteReader()!;
        var result = new Dictionary<int, OrderCuttingTime>();
        while (reader.Read())
        {
            var (id, value) = OrderExtrasData.Map(reader["OrderId"], reader["DurationMinutes"], reader["ParametersHash"], reader["FormatVersion"]);
            if (!result.TryAdd(id, value)) throw new OrderReadException("Повторяющиеся коды в таблице времени резки.");
        }
        return result;
    }

    internal static OrderCuttingTime? Read(OleDbConnection connection, OleDbTransaction transaction, int id) =>
        ReadOne(connection, transaction, id);

    internal static OrderCuttingTime? ReadOne(OleDbConnection connection, OleDbTransaction? transaction, int id)
    {
        if (!Exists(connection)) return null;
        Validate(connection, transaction);
        using var command = Command(connection, transaction); command.CommandText = $"SELECT * FROM [{TableName}] WHERE [OrderId]=?";
        command.Parameters.Add("id", OleDbType.Integer).Value = id;
        using var reader = command.ExecuteReader()!;
        if (!reader.Read()) return null;
        var (_, value) = OrderExtrasData.Map(reader["OrderId"], reader["DurationMinutes"], reader["ParametersHash"], reader["FormatVersion"]);
        if (reader.Read()) throw new OrderReadException("Повторяющиеся коды в таблице времени резки.");
        return value;
    }

    internal static void Write(OleDbConnection connection, OleDbTransaction transaction, int id, OrderCuttingTime? expected, OrderCuttingTime? value)
    {
        if (value == expected) return;
        if (!Exists(connection))
        {
            if (expected is null && value is null) return;
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Хранение времени ещё не подготовлено. Обновите список (F5); введённое время сохранено в форме.");
        }
        Validate(connection, transaction); value?.Validate();
        if (expected is not null)
        {
            using var delete = Command(connection, transaction);
            delete.CommandText = $"DELETE FROM [{TableName}] WHERE [OrderId]=? AND [DurationMinutes]=? AND StrComp([ParametersHash],?,0)=0 AND [FormatVersion]=1";
            delete.Parameters.Add("id", OleDbType.Integer).Value = id;
            delete.Parameters.Add("minutes", OleDbType.Decimal).Value = (decimal)expected.Minutes;
            delete.Parameters.Add("hash", OleDbType.VarWChar, 64).Value = expected.ParametersHash;
            if (delete.ExecuteNonQuery() != 1) throw OrderWriteException.Conflict();
        }
        else if (ReadOne(connection, transaction, id) is not null) throw OrderWriteException.Conflict();
        if (value is null) return;
        using var insert = Command(connection, transaction);
        insert.CommandText = $"INSERT INTO [{TableName}] ([OrderId],[DurationMinutes],[ParametersHash],[FormatVersion]) VALUES (?,?,?,1)";
        insert.Parameters.Add("id", OleDbType.Integer).Value = id;
        insert.Parameters.Add("minutes", OleDbType.Decimal).Value = (decimal)value.Minutes;
        insert.Parameters.Add("hash", OleDbType.VarWChar, 64).Value = value.ParametersHash;
        if (insert.ExecuteNonQuery() != 1) throw new InvalidOperationException("Время резки не подтверждено.");
    }

    private static OleDbCommand Command(OleDbConnection connection, OleDbTransaction? transaction)
    { var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandTimeout = 10; return command; }
}
