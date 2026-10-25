using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.Runtime.Versioning;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

[SupportedOSPlatform("windows")]
internal sealed class WorkflowMdbSession : IDisposable
{
    internal OleDbConnection Connection { get; }
    internal OleDbTransaction? Transaction { get; private set; }
    internal static string ConnectionString(string path, bool write)
    {
        var builder = new OleDbConnectionStringBuilder { Provider = "Microsoft.Jet.OLEDB.4.0", DataSource = path, PersistSecurityInfo = false };
        builder["Mode"] = write ? 19 : "Read"; builder["OLE DB Services"] = -2;
        return builder.ConnectionString;
    }
    internal WorkflowMdbSession(string path, bool write)
    { Connection = new(ConnectionString(path, write)); Connection.Open(); }
    internal void Begin() => Transaction = Connection.BeginTransaction(IsolationLevel.ReadCommitted);
    internal void Commit() { Transaction!.Commit(); Transaction.Dispose(); Transaction = null; }
    internal void Rollback() { Transaction!.Rollback(); Transaction.Dispose(); Transaction = null; }
    internal OleDbCommand Command(string sql, params (WorkflowMdbColumn Column, object? Value)[] values)
    {
        var command = new OleDbCommand(sql, Connection, Transaction) { CommandTimeout = 15 };
        foreach (var (column, value) in values)
        {
            var type = column.Type switch
            {
                WorkflowMdbType.Guid => OleDbType.Guid,
                WorkflowMdbType.Text => OleDbType.VarWChar,
                WorkflowMdbType.Memo => OleDbType.LongVarWChar,
                WorkflowMdbType.Boolean => OleDbType.Boolean,
                WorkflowMdbType.Int32 => OleDbType.Integer,
                WorkflowMdbType.Utc or WorkflowMdbType.Date => OleDbType.Date,
                // Jet DECIMAL columns use DBTYPE_NUMERIC, including decimal-backed
                // revisions and colours. DBTYPE_DECIMAL is a different OLE DB type.
                _ => OleDbType.Numeric
            };
            var parameter = command.Parameters.Add("p" + command.Parameters.Count, type);
            if (column.Type is WorkflowMdbType.Text) parameter.Size = column.Size;
            if (type == OleDbType.Numeric)
            { parameter.Precision = column.Type == WorkflowMdbType.UInt32 ? (byte)10 : column.Type == WorkflowMdbType.Int64 ? (byte)19 : (byte)28; parameter.Scale = column.Type == WorkflowMdbType.Decimal ? (byte)10 : (byte)0; }
            parameter.Value = value ?? DBNull.Value;
        }
        return command;
    }
    internal int Execute(string sql, params (WorkflowMdbColumn, object?)[] values)
    { using var command = Command(sql, values); return command.ExecuteNonQuery(); }
    internal object? Scalar(string sql, params (WorkflowMdbColumn, object?)[] values)
    { using var command = Command(sql, values); var value = command.ExecuteScalar(); return value is DBNull ? null : value; }
    internal Dictionary<Guid, WorkflowMdbRow> Read(string table, string? where = null, params (WorkflowMdbColumn, object?)[] values)
    {
        var columns = WorkflowMdbSchema.Tables.Single(t => t.Name == table).Columns;
        using var command = Command("SELECT " + string.Join(",", columns.Select(c => $"[{c.Name}]")) + $" FROM [{table}]" + (where is null ? "" : " WHERE " + where), values);
        using var reader = command.ExecuteReader(); var rows = new Dictionary<Guid, WorkflowMdbRow>();
        while (reader.Read())
        {
            var fields = new Dictionary<string, object?>();
            for (var i = 0; i < columns.Length; i++)
            {
                var column = columns[i]; object? value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                if (value is null && column.Required) throw new WorkflowValidationException($"Повреждено обязательное поле {table}.{column.Name}.");
                fields[column.Name] = value is null ? null : column.Type switch
                {
                    WorkflowMdbType.Guid => value is Guid guid ? guid : Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!),
                    WorkflowMdbType.Decimal or WorkflowMdbType.Int64 or WorkflowMdbType.UInt32 => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                    WorkflowMdbType.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                    WorkflowMdbType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                    WorkflowMdbType.Utc or WorkflowMdbType.Date => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
                    _ => Convert.ToString(value, CultureInfo.InvariantCulture)
                };
            }
            var id = (Guid)fields["id"]!; rows.Add(id, new(table, id, fields));
        }
        return rows;
    }
    internal void Insert(WorkflowMdbRow row)
    {
        var statement = WorkflowMdbStatements.Insert(row);
        Execute(statement.Sql, statement.Parameters);
    }
    internal void Update(WorkflowMdbRow before, WorkflowMdbRow after)
    {
        if (WorkflowMdbStatements.Update(before, after) is { } statement) Execute(statement.Sql, statement.Parameters);
    }
    internal static readonly WorkflowMdbColumn GuidColumn = new("id", null, WorkflowMdbType.Guid);
    internal static readonly WorkflowMdbColumn LongColumn = new("version", null, WorkflowMdbType.Int64);
    internal static readonly WorkflowMdbColumn TextColumn = new("text", null, WorkflowMdbType.Memo);
    internal static readonly WorkflowMdbColumn DateColumn = new("date", null, WorkflowMdbType.Utc);
    public void Dispose() { Transaction?.Dispose(); Connection.Dispose(); }
}
