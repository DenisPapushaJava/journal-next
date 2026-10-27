using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed record WorkflowMdbRow(string Table, Guid Id, Dictionary<string, object?> Values);

// JSON nodes are only a typed object mapper here. MDB columns contain scalar values, not JSON documents.
public static class WorkflowMdbMapping
{
    public static WorkflowMdbRow HandoffRow(Guid orderId, WorkflowShopHandoff stamp) => new("order_shop_handoffs", orderId, new()
    {
        ["id"] = orderId, ["order_version"] = (decimal)stamp.OrderVersion, ["actor_id"] = stamp.ActorId, ["transferred_at"] = stamp.At.UtcDateTime
    });
    public static IReadOnlyDictionary<string, Dictionary<Guid, WorkflowMdbRow>> Flatten(WorkflowWorkspace workspace, bool includeCatalog = true)
    {
        var result = WorkflowMdbSchema.Tables.ToDictionary(t => t.Name, _ => new Dictionary<Guid, WorkflowMdbRow>());
        void Add(string table, object value, Guid? owner = null)
        {
            var definition = WorkflowMdbSchema.Tables.Single(t => t.Name == table);
            var json = JsonSerializer.SerializeToNode(value)!;
            var values = new Dictionary<string, object?>();
            foreach (var column in definition.Columns)
                values[column.Name] = column.Path is null ? owner : ToDatabase(column, At(json, column.Path));
            var id = (Guid)values["id"]!; result[table].Add(id, new(table, id, values));
        }
        if (includeCatalog)
        {
            foreach (var e in workspace.Employees) Add("employees", e);
            foreach (var c in workspace.Customers) Add("customers", c);
            foreach (var m in workspace.Machines) Add("machines", m);
            foreach (var r in workspace.Rates) Add("cutting_rates", r);
            foreach (var key in workspace.PriceSource?.ImportedKeys ?? [])
            {
                var json = JsonSerializer.SerializeToNode(key)!.AsObject();
                var identity = JsonSerializer.Serialize(new { key.Machine, key.MaterialGroup, Thickness = key.ThicknessMm.ToString("G29", CultureInfo.InvariantCulture) });
                json["Id"] = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
                Add("price_import_keys", json);
            }
        }
        foreach (var order in workspace.Orders)
        {
            Add("orders", order);
            if (order.ShopHandoff is { } handoff) result["order_shop_handoffs"].Add(order.Id, HandoffRow(order.Id, handoff));
            foreach (var item in order.Items)
            {
                Add("order_items", item, order.Id);
                Add("order_item_selections", item, order.Id);
            }
            foreach (var project in order.Projects)
            {
                Add("cutting_projects", project, order.Id);
                if (project.Rename is { } rename) Add("project_renames", rename, project.Id);
            }
            foreach (var spec in order.Specifications)
            {
                if (!order.Projects.Any(p => p.Id == spec.Id))
                    throw new WorkflowValidationException("В MDB сохраняются спецификации подготовленных проектов. Перенос расчётов без проекта требует отдельной проверки.");
                Add("specifications", spec, order.Id);
                if (spec.Current.Report is { } report) Add("accepted_reports", report, spec.Id);
            }
        }
        return result;
    }
    public static bool Same(WorkflowMdbRow a, WorkflowMdbRow b) => a.Values.Count == b.Values.Count && a.Values.All(v => b.Values.TryGetValue(v.Key, out var value) && Equals(v.Value, value));

    public static WorkflowOrder[] Orders(IReadOnlyDictionary<string, Dictionary<Guid, WorkflowMdbRow>> data)
    {
        var reports = data["accepted_reports"].Values.ToDictionary(r => (Guid)r.Values["specification_id"]!);
        var renames = data["project_renames"].Values.ToDictionary(r => (Guid)r.Values["project_id"]!);
        var items = data["order_items"].Values.ToLookup(r => (Guid)r.Values["order_id"]!);
        var projects = data["cutting_projects"].Values.ToLookup(r => (Guid)r.Values["order_id"]!);
        var specs = data["specifications"].Values.ToLookup(r => (Guid)r.Values["order_id"]!);
        return data["orders"].Values.Select(row =>
        {
            var json = ToObject(row);
            if (data["order_shop_handoffs"].TryGetValue(row.Id, out var handoff)) json["ShopHandoff"] = ToObject(handoff);
            json["Items"] = Array(items[row.Id].OrderBy(r => r.Values["item_number"]).Select(item =>
            {
                if (!data["order_item_selections"].TryGetValue(item.Id, out var selected) ||
                    !Equals(selected.Values["order_id"], row.Id)) throw new WorkflowValidationException("В MDB не указан выбранный проект позиции.");
                var value = ToObject(item); value["CurrentProjectId"] = (Guid)selected.Values["project_id"]!; return value;
            }));
            json["Projects"] = Array(projects[row.Id].OrderBy(r => r.Values["variant_number"]).Select(r =>
            {
                var project = ToObject(r);
                if (renames.TryGetValue(r.Id, out var rename)) project["Rename"] = ToObject(rename);
                return project;
            }));
            json["Specifications"] = Array(specs[row.Id].Select(r =>
            {
                var spec = ToObject(r);
                if (reports.TryGetValue(r.Id, out var report)) spec["Current"]!["Report"] = ToObject(report);
                return spec;
            }));
            return json.Deserialize<WorkflowOrder>() ?? throw new WorkflowValidationException("Повреждена заявка MDB.");
        }).ToArray();
    }
    public static T Read<T>(WorkflowMdbRow row) => ToObject(row).Deserialize<T>() ?? throw new WorkflowValidationException("Повреждена строка MDB.");
    public static JsonObject ToObject(WorkflowMdbRow row)
    {
        var definition = WorkflowMdbSchema.Tables.Single(t => t.Name == row.Table); var json = new JsonObject();
        foreach (var column in definition.Columns.Where(c => c.Path is not null))
        {
            var value = row.Values[column.Name];
            JsonNode? node = value is null ? null : column.Type switch
            {
                WorkflowMdbType.Utc => JsonValue.Create(new DateTimeOffset(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc))),
                WorkflowMdbType.Date => JsonValue.Create(DateOnly.FromDateTime((DateTime)value)),
                WorkflowMdbType.Int64 => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
                WorkflowMdbType.UInt32 => JsonValue.Create(Convert.ToUInt32(value, CultureInfo.InvariantCulture)),
                _ => JsonSerializer.SerializeToNode(value)
            };
            Set(json, column.Path!, node);
        }
        return json;
    }
    public static object? ToDatabase(WorkflowMdbColumn column, JsonNode? node)
    {
        if (node is null)
        {
            if (column.Required) throw new WorkflowValidationException($"Не задано обязательное поле MDB: {column.Name}.");
            return null;
        }
        object value = column.Type switch
        {
            WorkflowMdbType.Guid => node.GetValue<Guid>(),
            WorkflowMdbType.Boolean => node.GetValue<bool>(),
            WorkflowMdbType.Utc => node.GetValue<DateTimeOffset>().UtcDateTime,
            WorkflowMdbType.Date => node.Deserialize<DateOnly>().ToDateTime(TimeOnly.MinValue),
            WorkflowMdbType.Int64 => (decimal)node.GetValue<long>(),
            WorkflowMdbType.UInt32 => (decimal)node.GetValue<uint>(),
            WorkflowMdbType.Int32 => node.GetValue<int>(),
            WorkflowMdbType.Decimal => node.GetValue<decimal>(),
            _ => node.GetValue<string>()
        };
        if (value is string text && column.Type == WorkflowMdbType.Text && text.Length > column.Size)
            throw new WorkflowValidationException($"Поле {column.Name} длиннее {column.Size} символов. Значение не сокращено.");
        if (value is decimal number && column.Type == WorkflowMdbType.Decimal) ValidateDecimal(column, number);
        return value;
    }
    internal static void ValidateDecimal(WorkflowMdbColumn column, decimal number)
    {
        if (decimal.Round(number, 10) != number || number >= 1000000000000000000m || number <= -1000000000000000000m)
            throw new WorkflowValidationException($"Поле {column.Name} выходит за точность MDB: до 18 целых и 10 дробных знаков. Значение не округлено.");
    }
    private static JsonArray Array(IEnumerable<JsonObject> values) => new(values.Select(v => (JsonNode)v).ToArray());
    private static JsonNode? At(JsonNode source, string path)
    { foreach (var part in path.Split('.')) { if (source[part] is not { } next) return null; source = next; } return source; }
    private static void Set(JsonObject target, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        for (var i = 0; i < parts.Length - 1; i++) { if (target[parts[i]] is not JsonObject child) target[parts[i]] = child = new(); target = child; }
        target[parts[^1]] = value;
    }
}
