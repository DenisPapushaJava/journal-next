using System.Data;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed partial class WorkflowMdbDatabase
{
    private sealed record Metadata(Guid DatabaseId, long Revision, long CatalogVersion, string? Root, string? PriceSource, DateTimeOffset? PriceLoadedAt);
    private static string PathOf(string path)
    {
        if (!OperatingSystem.IsWindows() || Environment.Is64BitProcess) throw new PlatformNotSupportedException("Для MDB требуется модуль Windows x86.");
        if (!Path.IsPathFullyQualified(path) || !Path.GetExtension(path).Equals(".mdb", StringComparison.OrdinalIgnoreCase))
            throw new WorkflowValidationException("Выберите полный путь к файлу MDB Journal Next.");
        return Path.GetFullPath(path);
    }
    public WorkflowAccessResponse Execute(WorkflowAccessRequest request)
    {
        if (request.Protocol != 1 || !Enum.IsDefined(request.Operation)) throw new WorkflowValidationException("Несовместимый запрос MDB.");
        var path = PathOf(request.Path);
        if (request.Operation == WorkflowAccessOperation.Create) return Create(path);
        if (!File.Exists(path)) throw new WorkflowValidationException("Файл базы Journal Next не найден или недоступен. Проверьте путь и сеть; новая база автоматически не создаётся.");
        if (request.Operation == WorkflowAccessOperation.Apply) return Apply(path, request);
        if (request.Operation == WorkflowAccessOperation.Purge) return Purge(path, request);
        return ReadSnapshot(path, request);
    }
    private static Metadata ReadMetadata(WorkflowMdbSession session, Guid? expected)
    {
        try
        {
            using var command = session.Command("SELECT [singleton],[product_id],[schema_version],[database_id],[revision],[catalog_revision],[project_root],[price_source],[price_loaded_at] FROM [app_metadata]");
            using var reader = command.ExecuteReader();
            if (!reader.Read() || Convert.ToInt32(reader[0], CultureInfo.InvariantCulture) != 1 || Convert.ToString(reader[1], CultureInfo.InvariantCulture) != WorkflowMdbSchema.ProductId)
                throw WrongDatabase();
            var schema = Convert.ToInt32(reader[2], CultureInfo.InvariantCulture);
            if (schema != WorkflowMdbSchema.Version) throw new WorkflowValidationException(schema > WorkflowMdbSchema.Version ?
                "База создана более новой версией Journal Next. Обновите программу." : "Версия этой MDB пока не поддерживается. Файл не изменён.");
            var id = (Guid)reader[3];
            if (id == Guid.Empty || expected is { } database && database != id) throw new WorkflowValidationException("По этому пути другая база Journal Next. Подключите её явно в настройках.");
            var meta = new Metadata(id, Convert.ToInt64(reader[4], CultureInfo.InvariantCulture), Convert.ToInt64(reader[5], CultureInfo.InvariantCulture),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null :
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(8), DateTimeKind.Utc)));
            if (meta.Revision < 0 || meta.CatalogVersion < 0 || reader.Read()) throw WrongDatabase();
            return meta;
        }
        catch (System.Data.OleDb.OleDbException ex)
        { throw new WorkflowValidationException("Не удалось проверить MDB Journal Next: файл занят, недоступен либо имеет другую структуру. Проверьте файл и доступ к папке. Код: " + ex.HResult.ToString("X8")); }
    }
    private static WorkflowValidationException WrongDatabase(string? detail = null) => new("Это не база Journal Next или её структура повреждена. Прежнюю MDB используйте только для импорта цен." +
        (detail is null ? "" : " Проверка: " + detail + "."));

    private static WorkflowAccessResponse Create(string target)
    {
        if (File.Exists(target)) throw new WorkflowValidationException("Этот файл уже существует. Подключите его или выберите новое имя; перезапись запрещена.");
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) throw new WorkflowValidationException("Папка для новой MDB не найдена или недоступна.");
        var temporary = Path.Combine(parent, ".JournalNext-create-" + Guid.NewGuid().ToString("N") + ".mdb");
        var publicationAttempted = false;
        var stage = "создание файла";
        try
        {
            WithCatalog(temporary, true, _ => { });
            var id = Guid.NewGuid();
            stage = "создание таблиц и индексов";
            using (var session = new WorkflowMdbSession(temporary, true))
            {
                foreach (var sql in WorkflowMdbSchema.CreateSql()) session.Execute(sql);
                session.Execute("INSERT INTO [app_metadata] ([singleton],[product_id],[schema_version],[database_id],[revision],[catalog_revision]) VALUES (1,?,1,?,0,0)",
                    (new("product", null, WorkflowMdbType.Text, true, 64), WorkflowMdbSchema.ProductId), (WorkflowMdbSession.GuidColumn, id));
                var data = WorkflowMdbMapping.Flatten(WorkflowWorkspace.Empty);
                foreach (var machine in data["machines"].Values) session.Insert(machine);
            }
            stage = "открытие структуры текстовых полей";
            WithCatalog(temporary, false, catalog =>
            {
                object? tables = null;
                try
                {
                    tables = catalog.Tables;
                    foreach (var table in WorkflowMdbSchema.Tables)
                    {
                        dynamic? columns = null; dynamic? item = null;
                        try
                        {
                            item = ((dynamic)tables)[table.Name]; columns = item.Columns;
                            foreach (var definition in table.Columns.Where(c => c.Type is WorkflowMdbType.Text or WorkflowMdbType.Memo))
                            {
                                stage = "настройка поля " + table.Name + "." + definition.Name;
                                dynamic? column = null; dynamic? property = null; dynamic? properties = null;
                                try { column = columns[definition.Name]; properties = column.Properties; property = properties["Jet OLEDB:Allow Zero Length"]; property.Value = true; }
                                finally { Release(property); Release(properties); Release(column); }
                            }
                        }
                        finally { Release(columns); Release(item); }
                    }
                }
                finally { Release(tables); }
            });
            stage = "проверка готовой структуры";
            var response = ReadSnapshot(temporary, new(1, WorkflowAccessOperation.Inspect, temporary, id));
            publicationAttempted = true;
            File.Move(temporary, target, overwrite: false);
            return response;
        }
        catch (COMException e)
        { throw new WorkflowValidationException("Не удалось подготовить MDB: " + stage + ". " + e.Message + " Код: " + e.HResult.ToString("X8")); }
        catch (System.Data.OleDb.OleDbException e)
        { throw new WorkflowValidationException("Не удалось подготовить MDB: " + stage + ". " + e.Message + " Код: " + e.HResult.ToString("X8")); }
        catch (IOException) when (publicationAttempted) { throw new WorkflowOutcomeUnknownException(); }
        finally
        {
            try { File.Delete(temporary); File.Delete(Path.ChangeExtension(temporary, ".ldb")); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Only this operation's temporary files are eligible for cleanup. */ }
        }
    }
    private static void WithCatalog(string path, bool create, Action<dynamic> use)
    {
        object? catalog = null; object? connection = null;
        try
        {
            var type = Type.GetTypeFromProgID("ADOX.Catalog", throwOnError: true)!;
            catalog = Activator.CreateInstance(type)!; dynamic value = catalog;
            // ADOX creation accepts initialization properties, not OleDb session settings.
            var builder = new System.Data.OleDb.OleDbConnectionStringBuilder
                { Provider = "Microsoft.Jet.OLEDB.4.0", DataSource = path };
            if (create) { builder["Jet OLEDB:Engine Type"] = 5; value.Create(builder.ConnectionString); }
            else value.ActiveConnection = builder.ConnectionString;
            connection = value.ActiveConnection; use(value);
        }
        finally
        {
            if (connection is not null) { try { ((dynamic)connection).Close(); } finally { Release(connection); } }
            Release(catalog);
        }
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    private static void ValidateSchema(WorkflowMdbSession session)
    {
        foreach (var table in new[] { new WorkflowMdbTable("app_metadata", WorkflowMdbSchema.Metadata) }.Concat(WorkflowMdbSchema.Tables))
        {
            using var command = session.Command($"SELECT * FROM [{table.Name}] WHERE 1=0");
            using var reader = command.ExecuteReader(CommandBehavior.SchemaOnly); var schema = reader.GetSchemaTable()!;
            if (reader.FieldCount != table.Columns.Length) throw WrongDatabase(table.Name + ": число полей " + reader.FieldCount);
            foreach (var column in table.Columns)
            {
                var ordinal = reader.GetOrdinal(column.Name); var field = reader.GetFieldType(ordinal);
                var expected = column.Type switch
                {
                    WorkflowMdbType.Guid => typeof(Guid),
                    WorkflowMdbType.Boolean => typeof(bool),
                    WorkflowMdbType.Int32 => typeof(int),
                    WorkflowMdbType.Int64 or WorkflowMdbType.UInt32 or WorkflowMdbType.Decimal => typeof(decimal),
                    WorkflowMdbType.Utc or WorkflowMdbType.Date => typeof(DateTime),
                    _ => typeof(string)
                };
                if (field != expected) throw WrongDatabase(table.Name + "." + column.Name + ": тип " + field.Name + ", ожидался " + expected.Name);
                var row = schema.Rows.Cast<DataRow>().Single(r => (string)r["ColumnName"] == column.Name);
                if (column.Required && row["AllowDBNull"] is true) throw WrongDatabase(table.Name + "." + column.Name + ": допускается NULL");
                if (column.Type == WorkflowMdbType.Text && Convert.ToInt32(row["ColumnSize"], CultureInfo.InvariantCulture) != column.Size) throw WrongDatabase(table.Name + "." + column.Name + ": размер " + row["ColumnSize"]);
                if (column.Type is WorkflowMdbType.Decimal or WorkflowMdbType.Int64 or WorkflowMdbType.UInt32 &&
                    (Convert.ToInt32(row["NumericPrecision"], CultureInfo.InvariantCulture) != (column.Type == WorkflowMdbType.Decimal ? 28 : column.Type == WorkflowMdbType.Int64 ? 19 : 10) ||
                     Convert.ToInt32(row["NumericScale"], CultureInfo.InvariantCulture) != (column.Type == WorkflowMdbType.Decimal ? 10 : 0))) throw WrongDatabase(table.Name + "." + column.Name + ": точность " + row["NumericPrecision"] + ", масштаб " + row["NumericScale"]);
            }
        }
        var indexes = session.Connection.GetOleDbSchemaTable(System.Data.OleDb.OleDbSchemaGuid.Indexes, null)!;
        foreach (var index in WorkflowMdbSchema.Indexes)
        {
            var rows = indexes.Rows.Cast<DataRow>().Where(r => string.Equals(Convert.ToString(r["TABLE_NAME"], CultureInfo.InvariantCulture), index.Table, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Convert.ToString(r["INDEX_NAME"], CultureInfo.InvariantCulture), index.Name, StringComparison.OrdinalIgnoreCase)).OrderBy(r => Convert.ToInt32(r["ORDINAL_POSITION"], CultureInfo.InvariantCulture)).ToArray();
            if (!rows.Select(r => Convert.ToString(r["COLUMN_NAME"], CultureInfo.InvariantCulture)).SequenceEqual(index.Columns, StringComparer.OrdinalIgnoreCase) ||
                index.Unique && rows.Any(r => r["UNIQUE"] is not true)) throw WrongDatabase("индекс " + index.Name);
        }
        foreach (var table in new[] { new WorkflowMdbTable("app_metadata", WorkflowMdbSchema.Metadata) }.Concat(WorkflowMdbSchema.Tables))
            if (!indexes.Rows.Cast<DataRow>().Any(r => string.Equals(Convert.ToString(r["TABLE_NAME"], CultureInfo.InvariantCulture), table.Name, StringComparison.OrdinalIgnoreCase) && r["PRIMARY_KEY"] is true)) throw WrongDatabase("первичный ключ " + table.Name);
        var keys = session.Connection.GetOleDbSchemaTable(System.Data.OleDb.OleDbSchemaGuid.Foreign_Keys, null)!;
        foreach (var key in WorkflowMdbSchema.ForeignKeys)
        {
            var rows = keys.Rows.Cast<DataRow>().Where(r => string.Equals(Convert.ToString(r["FK_NAME"], CultureInfo.InvariantCulture), key.Name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => Convert.ToInt32(r["ORDINAL"], CultureInfo.InvariantCulture)).ToArray();
            if (rows.Length != key.Columns.Length || rows.Any(r => !string.Equals(Convert.ToString(r["FK_TABLE_NAME"], CultureInfo.InvariantCulture), key.Table, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToString(r["PK_TABLE_NAME"], CultureInfo.InvariantCulture), key.Parent, StringComparison.OrdinalIgnoreCase)) ||
                !rows.Select(r => Convert.ToString(r["FK_COLUMN_NAME"], CultureInfo.InvariantCulture)).SequenceEqual(key.Columns, StringComparer.OrdinalIgnoreCase) ||
                !rows.Select(r => Convert.ToString(r["PK_COLUMN_NAME"], CultureInfo.InvariantCulture)).SequenceEqual(key.ParentColumns, StringComparer.OrdinalIgnoreCase)) throw WrongDatabase("связь " + key.Name + ", полей " + rows.Length);
        }
    }
}
