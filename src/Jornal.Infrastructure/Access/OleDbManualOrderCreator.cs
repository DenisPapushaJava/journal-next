using Jornal.Infrastructure.Diagnostics;
using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using System.Runtime.Versioning;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;

namespace Jornal.Infrastructure.Access;

[SupportedOSPlatform("windows")]
public sealed class OleDbManualOrderCreator(string path) : IManualOrderCreator, IPriceReader, IManualOrderBatchCreator, IMachineCatalogReader
{
    public Task<IReadOnlyList<CuttingMachine>> ReadMachinesAsync(CancellationToken cancellationToken = default) => new OleDbMachineCatalog(path).ReadMachinesAsync(cancellationToken);
    public Task<PriceTable> ReadAsync(string tableName, CancellationToken cancellationToken = default) =>
        OleDbPriceReader.ReadAsync(path, tableName, cancellationToken);

    public Task<OrderSummary> CreateAsync(ManualOrderDraft draft, CancellationToken cancellationToken = default) =>
        Task.Run(() => Create([draft], false, cancellationToken)[0], cancellationToken);

    public Task<IReadOnlyList<OrderSummary>> CreateBatchAsync(IReadOnlyList<ManualOrderDraft> drafts, CancellationToken cancellationToken = default)
    {
        var snapshot = ManualOrderBatch.Snapshot(drafts);
        return Task.Run(() => Create(snapshot, true, cancellationToken), cancellationToken);
    }

    private IReadOnlyList<OrderSummary> Create(IReadOnlyList<ManualOrderDraft> drafts, bool batch, CancellationToken cancellationToken)
    {
        var snapshot = ManualOrderBatch.Snapshot(drafts);
        if (snapshot.Any(draft => draft.CuttingDurationMinutes is not null))
            DiagnosticStages.RunAsync(DiagnosticStage.PrepareCuttingTime, () => new OleDbOrderExtras(path).PrepareCuttingTimeAsync(cancellationToken)).GetAwaiter().GetResult();
        if (snapshot.Any(draft => draft.HasBending || draft.Comment.Length > 0))
            new OleDbOrderAnnotations(path).PrepareAnnotationsAsync(cancellationToken).GetAwaiter().GetResult();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        OleDbConnection? connection = null;
        OleDbTransaction? transaction = null;
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
                var tables = new Dictionary<string, PriceTable>();
                var preparedRows = new List<OrderSummary>();
                for (var index = 0; index < snapshot.Length; index++)
                {
                    var draft = snapshot[index];
                    token.ThrowIfCancellationRequested();
                    var machine = OleDbMachineCatalog.Verify(connection, transaction, draft.CuttingType, draft.Machine, token);
                    var name = CuttingPriceCalculator.SelectTable(draft.PriceRequest, machine);
                    if (!tables.TryGetValue(name, out var table))
                        tables[name] = table = OleDbPriceReader.Read(connection, transaction, name, token);
                    try { preparedRows.Add(draft.ToOrder(0, DiagnosticStages.Run(DiagnosticStage.CalculatePrice, () => draft.Calculate(table, machine)).StoredPrice)); }
                    catch (OrderPriceChangedException changed) when (batch) { throw new OrderBatchPriceChangedException(index, changed.Quote); }
                }
                var savedRows = new List<OrderSummary>();
                foreach (var prepared in preparedRows)
                {
                    token.ThrowIfCancellationRequested();
                    using (var insert = Insert(connection, transaction, prepared))
                        if (DiagnosticStages.Run(DiagnosticStage.WriteRecord, insert.ExecuteNonQuery) != 1) throw new InvalidOperationException("INSERT не подтвердил одну новую заявку.");
                    int id;
                    using (var identity = connection.CreateCommand())
                    {
                        identity.Transaction = transaction;
                        identity.CommandTimeout = 10;
                        identity.CommandText = "SELECT @@IDENTITY";
                        id = checked(Convert.ToInt32(DiagnosticStages.Run(DiagnosticStage.ReadBack, identity.ExecuteScalar), System.Globalization.CultureInfo.InvariantCulture));
                        if (id <= 0) throw new InvalidOperationException("MDB не вернула код новой заявки.");
                    }
                    DiagnosticStages.Run(DiagnosticStage.WriteRecord, () => OleDbOrderExtras.Write(connection, transaction, id, null, prepared.CuttingTime));
                    DiagnosticStages.Run(DiagnosticStage.WriteRecord, () => OleDbOrderAnnotations.Write(connection, transaction, id, null, prepared.Annotations));
                    // До commit проверяем сохранённое представление. После commit к MDB больше не обращаемся.
                    var saved = DiagnosticStages.Run(DiagnosticStage.ReadBack, () => ReadCreated(connection, transaction, id));
                    if (saved != prepared with { Id = id }) throw new InvalidOperationException("MDB изменила значения новой заявки.");
                    savedRows.Add(saved);
                }
                token.ThrowIfCancellationRequested();
                commitStarted = true;
                DiagnosticStages.Run(DiagnosticStage.CommitTransaction, transaction.Commit);
                return savedRows;
            }
            catch (Exception exception)
            {
                var rollbackFailed = false;
                try { DiagnosticStages.Run(DiagnosticStage.RollbackTransaction, transaction.Rollback); }
                catch (Exception rollback) { rollbackFailed = true; AppDiagnostics.Capture(DiagnosticOperation.OrderCreate, rollback); }
                if (commitStarted || rollbackFailed)
                    throw new OrderWriteException(OrderWriteFailure.OutcomeUnknown,
                        "Не удалось подтвердить добавление. Заявки могли сохраниться. Обновите список и проверьте его перед повтором, чтобы не создать дубликат.", exception);
                throw;
            }
        }
        catch (OrderWriteException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException exception)
        {
            throw new OrderWriteException(OrderWriteFailure.NotSaved, "Превышено время добавления. Заявка не сохранена. Проверьте подключение к общей папке.", exception);
        }
        catch (Exception exception) when (exception is OleDbException or IOException or UnauthorizedAccessException or InvalidOperationException or OrderReadException or OverflowException)
        {
            throw new OrderWriteException(OrderWriteFailure.NotSaved,
                DataSourceAccessErrors.Describe(path, exception,
                    "Не удалось добавить заявку в MDB. Ввод сохранён. Проверьте права, подключение и блокировки Access."), exception);
        }
        finally { Release(transaction); Release(connection); }
    }

    private static OleDbCommand Insert(OleDbConnection connection, OleDbTransaction transaction, OrderSummary row)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 10;
        command.CommandText = "INSERT INTO [Orders] ([Дата], [Менеджер], [Спецификация], [Заказчик], [Вид_резки], [Марка_стали], " +
            "[Размер_листа], [Кол_во_листов], [Стоимость], [Толщина], [Ход], [Вес], [БЛЦБ], [Color], [Коэффициент]) " +
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)";
        Add(command, OleDbType.Date, row.StoredOrderDateTime);
        foreach (var text in new[] { row.ManagerName, row.SpecificationNumber, row.CustomerName, row.CuttingType,
                     row.MaterialName, row.SheetSize, row.SheetCount }) Add(command, OleDbType.VarWChar, text);
        Add(command, OleDbType.Currency, row.StoredCuttingPrice);
        Add(command, OleDbType.Double, (double)row.ThicknessMillimeters!.Value);
        Add(command, OleDbType.Integer, row.CutLengthMeters);
        Add(command, OleDbType.VarWChar, row.WeightKilograms);
        Add(command, OleDbType.VarWChar, row.DeliveryNumber);
        Add(command, OleDbType.Integer, row.Color);
        Add(command, OleDbType.VarWChar, row.StoredCoefficient);
        return command;
    }

    private static void Add(OleDbCommand command, OleDbType type, object? value)
    {
        var parameter = command.Parameters.Add("p" + command.Parameters.Count, type);
        if (type == OleDbType.VarWChar) parameter.Size = 255;
        parameter.Value = value ?? DBNull.Value;
    }

    private static OrderSummary ReadCreated(OleDbConnection connection, OleDbTransaction transaction, int id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 10;
        command.CommandText = "SELECT * FROM [Orders] WHERE [Код] = ?";
        Add(command, OleDbType.Integer, id);
        using var reader = command.ExecuteReader(CommandBehavior.SingleResult)!;
        if (!reader.Read()) throw new InvalidOperationException("Новая заявка не найдена до commit.");
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        AccessOrderMapper.ValidateColumns(columns);
        var row = AccessOrderMapper.Map(columns.Select((name, index) => (name, value: (object?)reader.GetValue(index)))
            .ToDictionary(pair => pair.name, pair => pair.value, StringComparer.Ordinal));
        reader.Close();
        return row with { CuttingTime = OleDbOrderExtras.Read(connection, transaction, id), Annotations = OleDbOrderAnnotations.ReadOne(connection, transaction, id) };
    }

    private static void Release(IDisposable? value)
    {
        try { DiagnosticStages.Run(DiagnosticStage.ReleaseResources, () => value?.Dispose()); }
        catch (Exception exception) { AppDiagnostics.Capture(DiagnosticOperation.OrderCreate, exception); }
    }
}
