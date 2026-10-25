using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed record WorkflowMdbQueryPlan(string SelectIds, string Count, (WorkflowMdbColumn Column, object? Value)[] Parameters,
    (WorkflowMdbColumn Column, object? Value)[] CountParameters);

public static class WorkflowMdbQueries
{
    public static WorkflowMdbQueryPlan Build(WorkflowReadQuery query, bool trash = false)
    {
        if (query.Limit is < 1 or > 200 || query.Search.Length > 512) throw new WorkflowValidationException("Проверьте размер страницы или длину поиска.");
        var terms = new WorkflowSearchQuery(query.Search); var words = query.Search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); if (words.Length > 8) throw new WorkflowValidationException("Для поиска в MDB используйте до 8 слов.");
        var where = new List<string> { trash ? "o.[deleted_at] IS NOT NULL" : "o.[deleted_at] IS NULL" };
        var values = new List<(WorkflowMdbColumn, object?)>();
        void Param(WorkflowMdbType type, object? value) => values.Add((new("p", null, type, false), value));
        if (terms.OrderReference is { } exact)
        { where.Add("o.[reference_number]=?"); Param(WorkflowMdbType.Text, exact); }
        else
        {
            if (query.From is { } from) { where.Add("o.[created_at]>=?"); Param(WorkflowMdbType.Utc, from.UtcDateTime); }
            if (query.Until is { } until) { where.Add("o.[created_at]<?"); Param(WorkflowMdbType.Utc, until.UtcDateTime); }
            foreach (var term in words)
            {
                var parts = new List<string>();
                string Contains(string field, bool number = false)
                {
                    Param(WorkflowMdbType.Memo, number ? term.Replace(',', '.').ToUpperInvariant() : term.ToUpperInvariant());
                    return "InStr(1,UCase(" + (number ? $"Replace(CStr({field}),',','.')" : field) + "),?,0)>0";
                }
                foreach (var field in new[] { "o.[reference_number]", "o.[blcb]" }) parts.Add(Contains(field));
                parts.Add(Contains("('Заявка ' & o.[reference_number])"));
                var time = "Mid(o.[reference_number],12,2) & ':' & Mid(o.[reference_number],14,2) & ':' & Mid(o.[reference_number],16)";
                foreach (var separator in new[] { " ", "-" })
                    parts.Add(Contains("(Left(o.[reference_number],10) & '" + separator + "' & " + time + ")"));
                parts.Add(Contains("(Mid(o.[reference_number],9,2) & '.' & Mid(o.[reference_number],6,2) & '.' & Left(o.[reference_number],4))"));
                parts.Add("(o.[blcb] IS NOT NULL AND " + Contains("('БЛЦБ ' & o.[blcb])") + ")");
                parts.Add("EXISTS (SELECT * FROM [customers] c WHERE c.[id]=o.[customer_id] AND " + Contains("c.[display_name]") + ")");
                foreach (var field in new[] { "manager_id", "technologist_id", "created_by" })
                    parts.Add($"EXISTS (SELECT * FROM [employees] e WHERE e.[id]=o.[{field}] AND " + Contains("e.[display_name]") + ")");
                parts.Add("EXISTS (SELECT * FROM [cutting_projects] p WHERE p.[order_id]=o.[id] AND p.[deleted_at] IS NULL AND (" +
                    Contains("p.[project_name]") + " OR " + Contains("(CStr(p.[thickness_mm]) & ' мм')", true) + " OR EXISTS (SELECT * FROM [machines] m WHERE m.[id]=p.[machine_id] AND " + Contains("m.[display_name]") + ")))");
                var specFields = new[] { "document_number", "material", "material_group", "blank_size", "comment", "machine_name" }.Select(f => Contains($"s.[{f}]")).ToList();
                specFields.Add(Contains("(CStr(s.[thickness_mm]) & ' мм')", true));
                specFields.Add(Contains("Format(s.[document_date],'dd.mm.yyyy')"));
                specFields.Add("EXISTS (SELECT * FROM [accepted_reports] r WHERE r.[specification_id]=s.[id] AND " + Contains("r.[file_name]") + ")");
                specFields.Add("EXISTS (SELECT * FROM [machines] m WHERE m.[id]=s.[machine_id] AND " + Contains("m.[display_name]") + ")");
                parts.Add("EXISTS (SELECT * FROM [specifications] s WHERE s.[order_id]=o.[id] AND EXISTS (SELECT * FROM [cutting_projects] sp WHERE sp.[id]=s.[id] AND sp.[deleted_at] IS NULL) AND (" + string.Join(" OR ", specFields) + "))");
                where.Add("(" + string.Join(" OR ", parts) + ")");
            }
        }
        var baseWhere = string.Join(" AND ", where); var countValues = values.ToArray();
        if (query.BeforeUpdated is { } before && query.BeforeNumber is { } numberCursor)
        {
            where.Add("(o.[updated_at]<? OR (o.[updated_at]=? AND o.[order_number]<?))");
            Param(WorkflowMdbType.Utc, before.UtcDateTime); Param(WorkflowMdbType.Utc, before.UtcDateTime); Param(WorkflowMdbType.Int64, (decimal)numberCursor);
        }
        return new($"SELECT TOP {query.Limit + 1} o.[id] FROM [orders] o WHERE " + string.Join(" AND ", where) + " ORDER BY o.[updated_at] DESC,o.[order_number] DESC",
            "SELECT COUNT(*) FROM [orders] o WHERE " + baseWhere, values.ToArray(), countValues);
    }
}
