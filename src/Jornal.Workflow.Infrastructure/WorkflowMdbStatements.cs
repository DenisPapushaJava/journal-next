using System.Globalization;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

internal sealed record WorkflowMdbStatement(string Sql, (WorkflowMdbColumn Column, object? Value)[] Parameters);

internal static class WorkflowMdbStatements
{
    internal static WorkflowMdbStatement Insert(WorkflowMdbRow row)
    {
        var columns = WorkflowMdbSchema.Tables.Single(t => t.Name == row.Table).Columns;
        var parameters = new List<(WorkflowMdbColumn, object?)>();
        var operands = columns.Select(c => Operand(c, row.Values[c.Name], parameters)).ToArray();
        return new($"INSERT INTO [{row.Table}] (" + string.Join(",", columns.Select(c => $"[{c.Name}]")) +
            ") VALUES (" + string.Join(",", operands) + ")", parameters.ToArray());
    }

    internal static WorkflowMdbStatement? Update(WorkflowMdbRow before, WorkflowMdbRow after)
    {
        var columns = WorkflowMdbSchema.Tables.Single(t => t.Name == after.Table).Columns.Where(c => c.Name != "id" &&
            !Equals(before.Values[c.Name], after.Values[c.Name])).ToArray();
        if (columns.Length == 0) return null;
        var parameters = new List<(WorkflowMdbColumn, object?)>();
        var assignments = columns.Select(c => $"[{c.Name}]=" + Operand(c, after.Values[c.Name], parameters)).ToArray();
        parameters.Add((new("id", null, WorkflowMdbType.Guid), after.Id));
        return new($"UPDATE [{after.Table}] SET " + string.Join(",", assignments) + " WHERE [id]=?", parameters.ToArray());
    }

    private static string Operand(WorkflowMdbColumn column, object? value, List<(WorkflowMdbColumn, object?)> parameters)
    {
        if (column.Type == WorkflowMdbType.Decimal && value is not null)
        {
            if (value is not decimal number) throw new WorkflowValidationException($"Поле {column.Name} должно быть числом decimal.");
            WorkflowMdbMapping.ValidateDecimal(column, number);
            // Jet rejects fractional Decimal/Numeric parameters on the reported Windows ru-RU system.
            // Only a validated CLR decimal becomes a numeric SQL token; user text stays parameterized.
            return number.ToString("0.############################", CultureInfo.InvariantCulture);
        }
        parameters.Add((column, value));
        return "?";
    }
}
