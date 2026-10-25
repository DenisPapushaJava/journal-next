using System.Globalization;
using System.Runtime.Versioning;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed partial class WorkflowMdbDatabase
{
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
        var after = ReadMetadata(session, metadata.DatabaseId);
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
        foreach (var table in new[] { "order_items", "cutting_projects", "specifications" }) Read(table, "order_id", found);
        Read("accepted_reports", "specification_id", data["specifications"].Keys);
        Read("project_renames", "project_id", data["cutting_projects"].Keys);
        return data;
    }
    private static WorkflowCatalog ReadCatalog(WorkflowMdbSession session, Metadata metadata, bool includeRates = true)
    {
        T[] Read<T>(string name) => session.Read(name).Values.Select(WorkflowMdbMapping.Read<T>).ToArray();
        return new(Read<Employee>("employees"), Read<Customer>("customers"), Read<WorkflowMachine>("machines"), includeRates ? Read<CuttingRate>("cutting_rates") : [],
            !includeRates || metadata.PriceSource is null ? null : new(metadata.PriceSource, metadata.PriceLoadedAt ?? throw WrongDatabase(), Read<WorkflowRateKey>("price_import_keys")), metadata.Root);
    }
}
