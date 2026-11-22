using System.Globalization;
using System.Runtime.Versioning;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed partial class WorkflowMdbDatabase
{
    private static WorkflowAccessResponse ReadSnapshot(string path, WorkflowAccessRequest request)
    {
        using var session = new WorkflowMdbSession(path, false);
        if (request.Operation is WorkflowAccessOperation.ReadOfficeOrders or WorkflowAccessOperation.ReadOfficeRevision or
            WorkflowAccessOperation.ReadOfficeProjects or WorkflowAccessOperation.ReadOfficeTechnologists)
            return ReadOfficeSnapshot(session, request);
        var metadata = ReadWorkspaceMetadata(session, request);
        if (request.Operation == WorkflowAccessOperation.ReadRevision)
            return new(1, metadata.DatabaseId, metadata.CatalogVersion, metadata.Revision, Orders: []);
        if (request.Operation == WorkflowAccessOperation.Inspect) ValidateSchema(session);
        return ReadResponse(session, metadata, request);
    }

    private static WorkflowAccessResponse ReadOfficeSnapshot(WorkflowMdbSession session, WorkflowAccessRequest request)
    {
        // These projections only use columns that exist from MDB schema 1.
        // Do not load or validate tables whose structure changed in later desktop versions.
        var metadata = ReadMetadata(session, request.DatabaseId, allowEarlierSchema: true);
        var snapshot = new WorkflowAccessResponse(1, metadata.DatabaseId, metadata.CatalogVersion, metadata.Revision,
            Orders: [], OfficeSchemaVersion: metadata.SchemaVersion);
        if (request.Operation == WorkflowAccessOperation.ReadOfficeRevision)
            return snapshot;
        if (request.Operation == WorkflowAccessOperation.ReadOfficeProjects)
            return snapshot with { OfficeProjects = ReadOfficeProjects(session, request.OrderIds) };
        if (request.Operation == WorkflowAccessOperation.ReadOfficeTechnologists)
        {
            using var command = session.Command("SELECT [id],[display_name] FROM [employees] WHERE [is_technologist]<>0 " +
                "AND [is_archived]=0 ORDER BY [display_name],[id]");
            using var reader = command.ExecuteReader();
            var people = new List<WorkflowOfficeTechnologist>();
            while (reader.Read()) people.Add(new(reader.GetGuid(0), reader.GetString(1)));
            return snapshot with
            {
                OfficeTechnologists = people.ToArray(),
                OfficeSchemaIsCurrent = metadata.SchemaVersion == WorkflowMdbSchema.Version
            };
        }

        const int pageSize = 20;
        var total = Convert.ToInt32(session.Scalar("SELECT COUNT(*) FROM [orders] WHERE [deleted_at] IS NULL"), CultureInfo.InvariantCulture);
        var rows = new List<WorkflowOfficeOrder>(pageSize);
        using (var command = session.Command($"SELECT TOP {pageSize} o.[id],o.[reference_number],c.[display_name],o.[row_version],o.[updated_at] " +
            "FROM [orders] o LEFT JOIN [customers] c ON o.[customer_id]=c.[id] WHERE o.[deleted_at] IS NULL ORDER BY o.[updated_at] DESC,o.[order_number] DESC"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    Convert.ToInt64(reader[3], CultureInfo.InvariantCulture), new(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc))));
        }

        var after = ReadMetadata(session, metadata.DatabaseId, allowEarlierSchema: true);
        if (after.Revision != metadata.Revision || after.CatalogVersion != metadata.CatalogVersion || after.SchemaVersion != metadata.SchemaVersion)
            throw new WorkflowConflictException();
        return snapshot with { TotalCount = total, HasMore = total > rows.Count, OfficeOrders = rows.ToArray() };
    }

    private static WorkflowOfficeProject[] ReadOfficeProjects(WorkflowMdbSession session, Guid[]? orderIds)
    {
        if (orderIds is not [var orderId] || orderId == Guid.Empty)
            throw new WorkflowValidationException("Укажите одну заявку для проверки обмена файлами.");
        using var command = session.Command("SELECT p.[id],p.[project_name],p.[native_path] FROM [cutting_projects] p " +
            "INNER JOIN [orders] o ON p.[order_id]=o.[id] WHERE o.[id]=? AND o.[deleted_at] IS NULL " +
            "AND p.[deleted_at] IS NULL AND p.[native_path] IS NOT NULL ORDER BY p.[created_at],p.[id]",
            (WorkflowMdbSession.GuidColumn, orderId));
        using var reader = command.ExecuteReader();
        var rows = new List<WorkflowOfficeProject>();
        while (reader.Read()) rows.Add(new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        return rows.ToArray();
    }

    private static WorkflowAccessResponse ReadResponse(WorkflowMdbSession session, Metadata metadata, WorkflowAccessRequest request)
    {
        Guid[] ids; Guid[] pageIds = []; var total = 0; var hasMore = false;
        if (request.Operation == WorkflowAccessOperation.ReadOrders) ids = CheckedIds(request.OrderIds);
        else if (request.Operation == WorkflowAccessOperation.Inspect) ids = [];
        else
        {
            var query = request.Query ?? new(); var plan = WorkflowMdbQueries.Build(query, request.Operation == WorkflowAccessOperation.ReadTrash);
            total = Convert.ToInt32(session.Scalar(plan.Count, plan.CountParameters), CultureInfo.InvariantCulture);
            using var command = session.Command(plan.SelectIds, plan.Parameters); using var reader = command.ExecuteReader();
            var found = new List<Guid>(); while (reader.Read()) found.Add((Guid)reader[0]);
            hasMore = found.Count > query.Limit;
            pageIds = found.Take(query.Limit).ToArray();
            ids = pageIds.Concat(CheckedIds(query.RetainIds)).Distinct().ToArray();
        }
        var orders = WorkflowMdbMapping.Orders(ReadOrders(session, ids));
        var catalog = request.KnownCatalogVersion == metadata.CatalogVersion ? null : ReadCatalog(session, metadata);
        var trashCount = Convert.ToInt32(session.Scalar("SELECT COUNT(*) FROM [orders] WHERE [deleted_at] IS NOT NULL"), CultureInfo.InvariantCulture);
        // Separate table reads must describe the same committed revision, including the catalogue.
        var after = ReadWorkspaceMetadata(session, request with { DatabaseId = metadata.DatabaseId });
        if (after.Revision != metadata.Revision || after.CatalogVersion != metadata.CatalogVersion) throw new WorkflowConflictException();
        return new(1, metadata.DatabaseId, metadata.CatalogVersion, metadata.Revision, catalog, orders, total, trashCount, hasMore, PageIds: pageIds);
    }
    private static Guid[] CheckedIds(Guid[]? ids)
    {
        ids ??= [];
        if (ids.Length > 512 || ids.Any(id => id == Guid.Empty)) throw new WorkflowValidationException("Слишком много открытых карточек для одного запроса.");
        return ids.Distinct().ToArray();
    }
    private static Dictionary<string, Dictionary<Guid, WorkflowMdbRow>> ReadOrders(WorkflowMdbSession session, Guid[] ids)
    {
        var data = WorkflowMdbSchema.Tables.ToDictionary(t => t.Name, _ => new Dictionary<Guid, WorkflowMdbRow>());
        void Read(string table, string column, IEnumerable<Guid> keys)
        {
            foreach (var batch in keys.Distinct().Chunk(75))
            {
                if (batch.Length == 0) continue;
                var rows = session.Read(table, $"[{column}] IN (" + string.Join(",", batch.Select(_ => "?")) + ")", batch.Select(id => (WorkflowMdbSession.GuidColumn, (object?)id)).ToArray());
                foreach (var row in rows) data[table].Add(row.Key, row.Value);
            }
        }
        Read("orders", "id", ids);
        var found = data["orders"].Keys.ToArray();
        Read("order_shop_handoffs", "id", found);
        foreach (var table in new[] { "order_items", "cutting_projects", "order_item_selections", "specifications" }) Read(table, "order_id", found);
        Read("accepted_reports", "specification_id", data["specifications"].Keys);
        Read("project_renames", "project_id", data["cutting_projects"].Keys);
        Read("project_shop_handoffs", "id", data["cutting_projects"].Keys);
        Read("project_shop_handoff_inputs", "id", data["project_shop_handoffs"].Keys);
        return data;
    }
    private static WorkflowCatalog ReadCatalog(WorkflowMdbSession session, Metadata metadata, bool includeRates = true)
    {
        T[] Read<T>(string name) => session.Read(name).Values.Select(WorkflowMdbMapping.Read<T>).ToArray();
        return new(Read<Employee>("employees"), Read<Customer>("customers"), Read<WorkflowMachine>("machines"), includeRates ? Read<CuttingRate>("cutting_rates") : [],
            !includeRates || metadata.PriceSource is null ? null : new(metadata.PriceSource, metadata.PriceLoadedAt ?? throw WrongDatabase(), Read<WorkflowRateKey>("price_import_keys")), metadata.Root);
    }
}
