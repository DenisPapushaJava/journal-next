using Jornal.Infrastructure.Diagnostics;
using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using System.Runtime.Versioning;
using Jornal.Core.Orders;

namespace Jornal.Infrastructure.Access;

[SupportedOSPlatform("windows")]
public sealed class OleDbOrderBatchEditor(string path) : IOrderBatchEditor
{
    public Task<IReadOnlyList<OrderSummary>> SetColorAsync(IReadOnlyCollection<OrderSummary> expected, int color,
        CancellationToken cancellationToken = default) => WriteAsync(OrderBatchChange.ForColor(expected, color), cancellationToken);

    public Task<IReadOnlyList<OrderSummary>> SetDeliveryNumberAsync(IReadOnlyCollection<OrderSummary> expected, string number,
        CancellationToken cancellationToken = default) => WriteAsync(OrderBatchChange.ForDeliveryNumber(expected, number), cancellationToken);

    private Task<IReadOnlyList<OrderSummary>> WriteAsync(OrderBatchChange change, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<OrderSummary>>(() => Write(change, cancellationToken), cancellationToken);

    private IReadOnlyList<OrderSummary> Write(OrderBatchChange change, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        OleDbTransaction? transaction = null;
        OleDbConnection? connection = null;
        var commitStarted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!string.Equals(Path.GetExtension(path), ".mdb", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new OrderWriteException(OrderWriteFailure.NotSaved, DataSourceAccessErrors.Unavailable(path));
            connection = DiagnosticStages.Run(DiagnosticStage.OpenDatabase, () => OleDbAccessConnection.Create(path, writable: true));
            DiagnosticStages.Run(DiagnosticStage.OpenDatabase, connection.Open);
            token.ThrowIfCancellationRequested();
            transaction = DiagnosticStages.Run(DiagnosticStage.BeginTransaction, () => connection.BeginTransaction(IsolationLevel.ReadCommitted));
            try
            {
                var updated = new List<OrderSummary>(change.Expected.Count);
                foreach (var expected in change.Expected)
                {
                    token.ThrowIfCancellationRequested();
                    using var command = CreateUpdate(connection, transaction, change, expected);
                    if (DiagnosticStages.Run(DiagnosticStage.WriteRecord, command.ExecuteNonQuery) != 1) throw OrderWriteException.Conflict();
                    // Результат читаем до commit: после успешного commit нет запросов, способных скрыть успех ошибкой.
                    updated.Add(DiagnosticStages.Run(DiagnosticStage.ReadBack, () => ReadRow(connection, transaction, expected.Id)));
                }
                token.ThrowIfCancellationRequested();
                commitStarted = true;
                DiagnosticStages.Run(DiagnosticStage.CommitTransaction, transaction.Commit);
                return updated;
            }
            catch (Exception exception)
            {
                var rollbackFailed = false;
                try { DiagnosticStages.Run(DiagnosticStage.RollbackTransaction, transaction.Rollback); }
                catch (Exception rollback) { rollbackFailed = true; AppDiagnostics.Capture(DiagnosticOperation.OrderBatch, rollback); }
                if (commitStarted || rollbackFailed)
                    throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown,
                        "Не удалось подтвердить сохранение. Изменения могли примениться. Закройте форму БЛЦБ, если она открыта, и обновите список (F5) перед повтором.", exception);
                throw;
            }
        }
        catch (OrderWriteException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException exception)
        {
            throw new OrderWriteException(OrderWriteFailure.NotSaved,
                "Превышено время сохранения. Пакет отменён. Проверьте доступ к общей папке и повторите действие.", exception);
        }
        catch (Exception exception) when (exception is OleDbException or IOException or UnauthorizedAccessException or InvalidOperationException or OrderReadException)
        {
            throw new OrderWriteException(OrderWriteFailure.NotSaved,
                DataSourceAccessErrors.Describe(path, exception,
                    "Не удалось сохранить пакет в MDB. Проверьте права на запись, подключение и блокировки Access. Пакет не сохранён."), exception);
        }
        finally
        {
            // Освобождение уже завершённого соединения не превращает успешный commit в отказ сохранения.
            Dispose(transaction);
            Dispose(connection);
        }
    }

    private static void Dispose(IDisposable? resource)
    {
        try { DiagnosticStages.Run(DiagnosticStage.ReleaseResources, () => resource?.Dispose()); }
        catch (Exception exception) { AppDiagnostics.Capture(DiagnosticOperation.OrderBatch, exception); }
    }

    private static OleDbCommand CreateUpdate(OleDbConnection connection, OleDbTransaction transaction,
        OrderBatchChange change, OrderSummary expected)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 10;
        var isColor = change.Field == OrderBatchField.Color;
        var column = isColor ? "Color" : "БЛЦБ";
        object? original = isColor ? expected.Color : expected.DeliveryNumber;
        object? value = isColor ? change.Color : change.DeliveryNumber;
        var type = isColor ? OleDbType.Integer : OleDbType.VarWChar;
        var condition = original is null ? $"[{column}] IS NULL" : isColor
            ? "[Color] = ?" : "StrComp([БЛЦБ], ?, 0) = 0";
        command.CommandText = $"UPDATE [Orders] SET [{column}] = ? WHERE [Код] = ? AND {condition}";
        Add(command, type, value);
        Add(command, OleDbType.Integer, expected.Id);
        if (original is not null) Add(command, type, original);
        return command;
    }

    private static void Add(OleDbCommand command, OleDbType type, object? value)
    {
        var parameter = command.Parameters.Add("p" + command.Parameters.Count, type);
        if (type == OleDbType.VarWChar) parameter.Size = 255;
        parameter.Value = value ?? DBNull.Value;
    }

    private static OrderSummary ReadRow(OleDbConnection connection, OleDbTransaction transaction, int id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM [Orders] WHERE [Код] = ?";
        command.CommandTimeout = 10;
        Add(command, OleDbType.Integer, id);
        using var reader = command.ExecuteReader(CommandBehavior.SingleResult)!;
        if (!reader.Read()) throw OrderWriteException.Conflict();
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        AccessOrderMapper.ValidateColumns(names);
        var row = AccessOrderMapper.Map(names.Select((name, index) => (name, value: reader.GetValue(index)))
            .ToDictionary(item => item.name, item => (object?)item.value, StringComparer.Ordinal));
        reader.Close();
        row = row with { CuttingTime = OleDbOrderExtras.Read(connection, transaction, id) };
        try { return row with { Annotations = OleDbOrderAnnotations.ReadOne(connection, transaction, id) }; }
        catch (Exception exception) when (exception is OrderReadException or OleDbException or InvalidOperationException)
        { return row with { AnnotationsUnavailable = true }; }
    }
}
