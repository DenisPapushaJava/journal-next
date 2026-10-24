using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Infrastructure.Access;

// ADOX меняет имя самого поля: его тип, позиция, NULL/default и индексы не пересоздаются.
// ADO и ADOX используют одну COM-сессию Jet и одну транзакцию.
[SupportedOSPlatform("windows")]
internal sealed class JetPriceRenamer(string path)
{
    private sealed record RenameCapability(bool Supported, Exception? Error = null);
    private static readonly Lazy<RenameCapability> TransactionalRename = new(VerifyTransactionalRename);

    public PriceTable Save(PriceTableChange change, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        JetPriceSession? session = null;
        var started = false; var commitStarted = false; var schemaStarted = false;
        try
        {
            token.ThrowIfCancellationRequested();
            if (change.ConfirmedUsage is null)
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "Проверьте и подтвердите последствия изменения толщин.");
            if (Environment.Is64BitProcess || !File.Exists(path) || !Path.GetExtension(path).Equals(".mdb", StringComparison.OrdinalIgnoreCase))
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "Изменение толщин MDB доступно через поставляемый модуль Jet x86.");
            // Проверка отката на отдельной временной MDB до любого изменения пользовательского файла.
            var capability = DiagnosticStages.Run(DiagnosticStage.OpenDatabase, () => TransactionalRename.Value);
            if (!capability.Supported)
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "Jet не подтвердил безопасное переименование толщин. База не изменена.", capability.Error);
            DiagnosticStages.Run(DiagnosticStage.OpenDatabase, () => AccessPriceBackup.Create(path, token, "price-edit"));
            token.ThrowIfCancellationRequested(); session = DiagnosticStages.Run(DiagnosticStage.OpenDatabase, () => JetPriceSession.Open(path));
            var machines = session.ReadMachines(token);
            if (CuttingMachine.IsCustomTable(change.Expected.Name) && !machines.Any(machine => machine.TableName == change.Expected.Name))
                throw new PriceReadException("Прайс отсутствует в каталоге станков.");
            DiagnosticStages.Run(DiagnosticStage.BeginTransaction, session.Begin); started = true;
            var fieldCount = 0;
            var original = DiagnosticStages.Run(DiagnosticStage.ReadPrices, () => session.ReadPrice(change.Expected.Name, token, out fieldCount));
            if (!PriceTableChange.Matches(original, change.Expected)) throw PriceTableChange.Conflict();
            DiagnosticStages.Run(DiagnosticStage.ReadOrders, () => change.VerifyUsage(PriceThicknessUsage.Count(change, session.ReadUsage(token), machines)));
            if (fieldCount + change.AddedRates.Count() > 255)
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "В таблице MDB не может быть больше 255 полей.");
            foreach (var (before, after) in change.RenamedRates)
            { token.ThrowIfCancellationRequested(); schemaStarted = true; DiagnosticStages.Run(DiagnosticStage.WriteRecord, () => session.Rename(original.Name, before.ColumnName, after.ColumnName)); }
            foreach (var rate in change.AddedRates)
            { token.ThrowIfCancellationRequested(); schemaStarted = true; DiagnosticStages.Run(DiagnosticStage.WriteRecord, () => session.Execute($"ALTER TABLE {Quote(original.Name)} ADD COLUMN {Quote(rate.ColumnName)} LONG")); }
            var changed = original.Rates.Zip(change.Values.Rates).Where(pair => pair.First.RublesPerMeter != pair.Second.RublesPerMeter)
                .Select(pair => pair.Second).Concat(change.AddedRates).ToArray();
            if (changed.Length > 0) DiagnosticStages.Run(DiagnosticStage.WriteRecord, () => session.Update(original.Name, changed));
            var saved = DiagnosticStages.Run(DiagnosticStage.ReadBack, () => session.ReadPrice(original.Name, token));
            if (!PriceTableChange.Matches(saved, change.Values)) throw new InvalidOperationException("Прайс отличается от подтверждённого изменения.");
            token.ThrowIfCancellationRequested(); commitStarted = true; DiagnosticStages.Run(DiagnosticStage.CommitTransaction, session.Commit); started = false;
            return saved;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var uncertain = commitStarted;
            if (started)
            {
                try { DiagnosticStages.Run(DiagnosticStage.RollbackTransaction, session!.Rollback); }
                catch (Exception rollback) { uncertain = true; AppDiagnostics.Capture(DiagnosticOperation.AccessHost, rollback); }
                if (schemaStarted && !uncertain)
                    try { uncertain = !PriceTableChange.Matches(change.Expected, session!.ReadPrice(change.Expected.Name, CancellationToken.None)); }
                    catch (Exception) { uncertain = true; }
            }
            if (!uncertain && exception is OrderWriteException) throw;
            if (!uncertain && exception is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            throw new OrderWriteException(uncertain ? OrderWriteFailure.OutcomeUnknown : OrderWriteFailure.NotSaved,
                uncertain ? "Сохранение прайса не подтверждено. Обновите прайс перед повтором; изменения могли примениться." :
                    "Толщины не изменены. Закройте подключения к MDB на остальных ПК и проверьте доступ к её папке. Ввод сохранён.", exception);
        }
        finally { session?.Dispose(); }
    }

    private static RenameCapability VerifyTransactionalRename()
    {
        var folder = Path.Combine(Path.GetTempPath(), "journal-jet-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); var file = Path.Combine(folder, "probe.mdb");
        try
        {
            JetPriceSession.Create(file);
            using var session = JetPriceSession.Open(file);
            session.Execute("CREATE TABLE [PriceP] ([Код] COUNTER PRIMARY KEY, [2,5] LONG NOT NULL, [3] LONG)");
            session.Execute("INSERT INTO [PriceP] ([2,5]) VALUES (17)");
            var before = session.ReadPrice(PriceTables.Plasma, CancellationToken.None);
            var renamed = before with { Rates = [new(2.8m, "2,8", 17), .. before.Rates.Skip(1)] };
            session.Begin();
            session.Rename(PriceTables.Plasma, "2,5", "2,8");
            session.Update(PriceTables.Plasma, [new(2.8m, "2,8", 23)]);
            if (!PriceTableChange.Matches(renamed with { Rates = [new(2.8m, "2,8", 23), .. before.Rates.Skip(1)] }, session.ReadPrice(PriceTables.Plasma, CancellationToken.None)))
                throw new InvalidOperationException("Jet не подтвердил пробное изменение поля и тарифа.");
            session.Rollback();
            if (!PriceTableChange.Matches(before, session.ReadPrice(PriceTables.Plasma, CancellationToken.None)))
                throw new InvalidOperationException("Jet не подтвердил откат пробного изменения.");
            session.Begin(); session.Rename(PriceTables.Plasma, "2,5", "2,8"); session.Commit();
            if (!PriceTableChange.Matches(renamed, session.ReadPrice(PriceTables.Plasma, CancellationToken.None)))
                throw new InvalidOperationException("Jet не подтвердил сохранение пробного изменения.");
            return new(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return new(false, exception); }
        finally { try { Directory.Delete(folder, true); } catch (Exception) { } }
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private sealed class ComScope(object? value) : IDisposable
    {
        public dynamic Value { get; } = value!;
        public void Dispose() { if ((object?)Value is { } instance && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
        public static ComScope Create(string progId) => new(Activator.CreateInstance(Type.GetTypeFromProgID(progId, true)!)!);
    }

    private sealed class JetPriceSession : IDisposable
    {
        private readonly ComScope _connection;
        private readonly ComScope _catalog;
        private JetPriceSession(string path)
        {
            _connection = ComScope.Create("ADODB.Connection");
            try
            {
                _connection.Value.ConnectionTimeout = 10; _connection.Value.CommandTimeout = 10;
                _connection.Value.Open(ConnectionString(path));
                _catalog = ComScope.Create("ADOX.Catalog"); _catalog.Value.ActiveConnection = _connection.Value;
            }
            catch
            {
                // ActiveConnection может отказать уже после создания COM-каталога.
                try { _catalog?.Dispose(); } catch (Exception) { }
                _connection.Dispose(); throw;
            }
        }
        private static string ConnectionString(string path) => new System.Data.OleDb.OleDbConnectionStringBuilder
            { Provider = "Microsoft.Jet.OLEDB.4.0", DataSource = Path.GetFullPath(path), ["Mode"] = "Share Exclusive", ["OLE DB Services"] = -2 }.ConnectionString;
        public static JetPriceSession Open(string path) => new(path);
        public static void Create(string path)
        {
            using var catalog = ComScope.Create("ADOX.Catalog");
            // ADOX.Create принимает параметры создания, а не Mode/OLE DB Services существующей сессии.
            catalog.Value.Create(new System.Data.OleDb.OleDbConnectionStringBuilder
                { Provider = "Microsoft.Jet.OLEDB.4.0", DataSource = Path.GetFullPath(path), ["Jet OLEDB:Engine Type"] = 5 }.ConnectionString);
            using var connection = new ComScope(catalog.Value.ActiveConnection); connection.Value.Close();
        }
        public void Begin() { _connection.Value.IsolationLevel = 4096; if ((int)_connection.Value.BeginTrans() != 1) throw new InvalidOperationException("Вложенная транзакция Jet."); }
        public void Commit() => _connection.Value.CommitTrans();
        public void Rollback() => _connection.Value.RollbackTrans();
        public void Execute(string sql)
        {
            object affected;
            using var result = new ComScope(_connection.Value.Execute(sql, out affected, 128));
        }
        public void Rename(string tableName, string before, string after)
        {
            using var tables = new ComScope(_catalog.Value.Tables);
            tables.Value.Refresh();
            using var table = new ComScope(tables.Value[tableName]);
            using var columns = new ComScope(table.Value.Columns);
            columns.Value.Refresh();
            using var column = new ComScope(columns.Value[before]);
            column.Value.Name = after;
        }
        public void Update(string tableName, IReadOnlyList<PriceRate> rates)
        {
            using var command = ComScope.Create("ADODB.Command");
            command.Value.ActiveConnection = _connection.Value; command.Value.CommandType = 1; command.Value.CommandTimeout = 10;
            command.Value.CommandText = $"UPDATE {Quote(tableName)} SET " + string.Join(", ", rates.Select(rate => Quote(rate.ColumnName) + " = ?"));
            for (var index = 0; index < rates.Count; index++)
            {
                // До первого CreateParameter нельзя читать Count: ADO автоматически выведет
                // незаполненные параметры из SQL, и Append добавит к ним ещё один набор.
                using var parameter = new ComScope(command.Value.CreateParameter("p" + index, 3, 1, 4, (object?)rates[index].RublesPerMeter ?? DBNull.Value));
                using var parameters = new ComScope(command.Value.Parameters);
                parameters.Value.Append(parameter.Value);
            }
            using (var parameters = new ComScope(command.Value.Parameters))
                if ((int)parameters.Value.Count != rates.Count) throw new InvalidOperationException("ADO не подтвердил параметры тарифов.");
            object affected; command.Value.Execute(out affected, Type.Missing, 128);
            if (Convert.ToInt32(affected, CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("Jet не подтвердил одну строку прайса.");
        }
        private IEnumerable<(string[] Names, object?[] Values, int[] Types)> Read(string sql, CancellationToken token)
        {
            using var rows = ComScope.Create("ADODB.Recordset");
            rows.Value.Open(sql, _connection.Value, 0, 1, 1);
            try
            {
                using var fields = new ComScope(rows.Value.Fields);
                var count = (int)fields.Value.Count; var names = new string[count]; var types = new int[count];
                for (var i = 0; i < count; i++) { using var field = new ComScope(fields.Value[i]); names[i] = (string)field.Value.Name; types[i] = (int)field.Value.Type; }
                while (!(bool)rows.Value.EOF)
                {
                    token.ThrowIfCancellationRequested(); var values = new object?[count];
                    for (var i = 0; i < count; i++) { using var field = new ComScope(fields.Value[i]); values[i] = field.Value.Value; }
                    yield return (names, values, types); rows.Value.MoveNext();
                }
            }
            finally { rows.Value.Close(); }
        }
        public PriceTable ReadPrice(string name, CancellationToken token) => ReadPrice(name, token, out _);
        public PriceTable ReadPrice(string name, CancellationToken token, out int fieldCount)
        {
            var rows = Read($"SELECT * FROM {Quote(name)}", token).Take(2).ToArray();
            if (rows.Length != 1) throw new OrderWriteException(OrderWriteFailure.NotSaved, "Прайс должен содержать ровно одну строку.");
            fieldCount = rows[0].Names.Length;
            var table = AccessPriceMapper.Map(name, rows[0].Names, rows[0].Values);
            if (table.Rates.Any(rate => rows[0].Types[Array.IndexOf(rows[0].Names, rate.ColumnName)] != 3))
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "Тип тарифа отличается от Long Integer. База не изменена.");
            return table;
        }
        public IEnumerable<PriceUsageOrder> ReadUsage(CancellationToken token) =>
            Read("SELECT [Вид_резки], [Толщина], [Коэффициент] FROM [Orders]", token).Select(row =>
                PriceUsageOrder.FromStored(row.Values[0] as string, row.Values[1] is null or DBNull ? null : Convert.ToDecimal(row.Values[1]), row.Values[2] as string));
        public CuttingMachine[] ReadMachines(CancellationToken token)
        {
            using var tables = new ComScope(_catalog.Value.Tables);
            var exists = false;
            for (var i = 0; i < (int)tables.Value.Count; i++)
            { using var table = new ComScope(tables.Value[i]); if ((string)table.Value.Name == CuttingMachine.CatalogTable) { exists = true; break; } }
            if (!exists) return [];
            var rows = Read("SELECT [Name], [PriceTableName], [SpecificationProfileId] FROM [JournalMachines]", token).Take(129).ToArray();
            return CuttingMachine.Snapshot(rows.Select(row => new CuttingMachine(row.Values[0] as string ?? "", row.Values[1] as string ?? "", row.Values[2] as string)));
        }
        public void Dispose()
        {
            try { _catalog.Dispose(); } catch (Exception) { }
            try { _connection.Value.Close(); } catch (Exception) { }
            try { _connection.Dispose(); } catch (Exception) { }
        }
    }
}
