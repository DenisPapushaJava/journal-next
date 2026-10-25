using System.Globalization;
using System.Runtime.Versioning;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed partial class WorkflowMdbDatabase
{
    private static readonly string[] CatalogTables = ["employees", "customers", "machines", "cutting_rates", "price_import_keys"];
    private static readonly string[] OrderTables = ["orders", "order_items", "cutting_projects", "order_item_selections", "specifications", "accepted_reports", "project_renames"];

    private static WorkflowAccessResponse Apply(string path, WorkflowAccessRequest request)
    {
        var patch = request.Patch ?? throw new WorkflowValidationException("Не переданы изменения MDB.");
        if (patch.Orders.Length > 200 || patch.Orders.Select(o => o.Id).Distinct().Count() != patch.Orders.Length || patch.ExpectedCatalogVersion < 0)
            throw new WorkflowValidationException("Некорректный пакет изменений MDB.");
        var commitAttempted = false;
        using var session = new WorkflowMdbSession(path, true);
        try
        {
            // Read identity before the transaction so concurrent writers do not upgrade shared metadata locks.
            var original = ReadMetadata(session, request.DatabaseId); session.Begin();
            if (patch.ExpectedVersion is { } version && original.Revision != version) throw new WorkflowConflictException();
            // Acquire one short write token before touching business rows. Never hold it for UI or report I/O.
            var sql = "UPDATE [app_metadata] SET [revision]=[revision]+1 WHERE [singleton]=1 AND [database_id]=?";
            var args = new List<(WorkflowMdbColumn, object?)> { (WorkflowMdbSession.GuidColumn, original.DatabaseId) };
            if (patch.ExpectedVersion is { } expected) { sql += " AND [revision]=?"; args.Add((WorkflowMdbSession.LongColumn, (decimal)expected)); }
            if (session.Execute(sql, args.ToArray()) != 1) throw new WorkflowConflictException();
            var meta = ReadMetadata(session, request.DatabaseId);
            if (meta.CatalogVersion != patch.ExpectedCatalogVersion) throw new WorkflowConflictException();
            var currentOrders = WorkflowMdbMapping.Orders(ReadOrders(session, CheckedIds(patch.Orders.Select(o => o.Id).ToArray())));
            var changed = new List<WorkflowOrder>();
            var nextNumber = patch.Orders.Any(o => o.ExpectedVersion is null) ? Convert.ToInt64(session.Scalar("SELECT MAX([order_number]) FROM [orders]") ?? 0, CultureInfo.InvariantCulture) : 0;
            var allocated = new HashSet<string>(StringComparer.Ordinal);
            foreach (var replacement in patch.Orders.OrderBy(o => o.Id))
            {
                var current = currentOrders.SingleOrDefault(o => o.Id == replacement.Id);
                if (current?.Version != replacement.ExpectedVersion) throw new WorkflowConflictException();
                if (current?.IsDeleted == true && !replacement.AllowTrashed) throw new WorkflowValidationException("Заявка находится в корзине.");
                if (replacement.Value is not { } value)
                {
                    if (current?.IsDeleted != true || !replacement.AllowTrashed) throw new WorkflowValidationException("Окончательное удаление доступно только в корзине.");
                    continue;
                }
                if (value.Id != replacement.Id) throw new WorkflowValidationException("Некорректный ID заявки.");
                if (current is null)
                {
                    var at = value.CreatedAt; var lower = at.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
                    var upper = at.AddSeconds(1).ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
                    using var command = session.Command("SELECT [reference_number] FROM [orders] WHERE [reference_number]>=? AND [reference_number]<?",
                        (new("reference", null, WorkflowMdbType.Text, true, 40), lower), (new("reference", null, WorkflowMdbType.Text, true, 40), upper));
                    using (var reader = command.ExecuteReader()) while (reader.Read()) allocated.Add(reader.GetString(0));
                    value = value with { Number = checked(++nextNumber), ReferenceNumber = WorkflowOrderNumbers.Allocate(allocated, at) };
                    allocated.Add(value.ReferenceNumber);
                    if (value.Version != 1 || value.IsDeleted) throw new WorkflowValidationException("Некорректная новая заявка.");
                }
                else if (value.Version <= current.Version || value.Number != current.Number || value.ReferenceNumber != current.ReferenceNumber ||
                    value.CreatedAt != current.CreatedAt || value.CreatedById != current.CreatedById)
                    throw new WorkflowValidationException("Некорректная версия или неизменяемые реквизиты заявки.");
                changed.Add(value);
            }
            var catalog = ReadCatalog(session, meta, patch.Catalog is not null);
            var nextCatalog = patch.Catalog ?? catalog;
            var before = catalog.WithOrders(meta.Revision, currentOrders);
            var after = nextCatalog.WithOrders(meta.Revision, changed.ToArray()); after.Validate();
            foreach (var specification in after.Orders.SelectMany(o => o.Specifications))
            {
                var p = specification.Current.Parameters; var calculation = specification.Current.Calculation;
                var rate = new CuttingRate(calculation.RateId, p.Machine, p.MaterialGroup, p.ThicknessMm, calculation.PricePerMeter, calculation.PricePerPierce) { MachineId = p.MachineId };
                if (CuttingCalculation.Calculate(p, rate) != calculation) throw new WorkflowValidationException("Сохранённая стоимость не соответствует параметрам расчёта.");
            }
            var oldRows = WorkflowMdbMapping.Flatten(before, patch.Catalog is not null);
            var newRows = WorkflowMdbMapping.Flatten(after, patch.Catalog is not null);
            var tables = (patch.Catalog is null ? OrderTables : CatalogTables.Concat(OrderTables)).ToArray();
            var anyRows = tables.Any(t => oldRows[t].Count != newRows[t].Count || newRows[t].Any(r => !oldRows[t].TryGetValue(r.Key, out var old) || !WorkflowMdbMapping.Same(old, r.Value)));
            var metadataChange = patch.Catalog is not null && (meta.Root != nextCatalog.ProjectRoot || meta.PriceSource != nextCatalog.PriceSource?.Path || meta.PriceLoadedAt != nextCatalog.PriceSource?.LoadedAt);
            if (!anyRows && !metadataChange)
            {
                var unchanged = ReadResponse(session, meta, request with
                    { Operation = WorkflowAccessOperation.ReadOrders, OrderIds = patch.Orders.Select(o => o.Id).ToArray() });
                session.Rollback(); return unchanged with { Revision = meta.Revision - 1 };
            }

            // Remove changed selections before their parents, then insert them after the projects.
            // No partially-null composite FK or circular insertion is needed in Jet.
            foreach (var selected in oldRows["order_item_selections"].Values.Where(row =>
                !newRows["order_item_selections"].TryGetValue(row.Id, out var next) || !WorkflowMdbMapping.Same(row, next)).ToArray())
            {
                session.Execute("DELETE FROM [order_item_selections] WHERE [id]=?", (WorkflowMdbSession.GuidColumn, selected.Id));
                oldRows["order_item_selections"].Remove(selected.Id);
            }
            foreach (var table in new[] { "accepted_reports", "specifications", "project_renames", "cutting_projects", "order_items", "orders" })
                foreach (var row in oldRows[table].Values.Where(r => !newRows[table].ContainsKey(r.Id)).OrderBy(r => r.Id))
                    session.Execute($"DELETE FROM [{table}] WHERE [id]=?", (WorkflowMdbSession.GuidColumn, row.Id));
            if (patch.Catalog is not null)
            {
                foreach (var machine in oldRows["machines"].Values.Where(r => !newRows["machines"].ContainsKey(r.Id)))
                    foreach (var table in new[] { "cutting_projects", "specifications", "project_renames" })
                        if (Convert.ToInt32(session.Scalar($"SELECT COUNT(*) FROM [{table}] WHERE [machine_id]=?", (WorkflowMdbSession.GuidColumn, machine.Id)), CultureInfo.InvariantCulture) > 0)
                            throw new WorkflowValidationException("Станок используется в проектах или расчётах других заявок. Удаление недоступно.");
                foreach (var table in new[] { "price_import_keys", "cutting_rates", "machines", "customers", "employees" })
                    foreach (var row in oldRows[table].Values.Where(r => !newRows[table].ContainsKey(r.Id)).OrderBy(r => r.Id))
                        session.Execute($"DELETE FROM [{table}] WHERE [id]=?", (WorkflowMdbSession.GuidColumn, row.Id));
            }
            foreach (var table in (patch.Catalog is null ? Array.Empty<string>() : CatalogTables).Concat(OrderTables))
                foreach (var row in newRows[table].Values.OrderBy(r => r.Id))
                {
                    if (!oldRows[table].TryGetValue(row.Id, out var old))
                        session.Insert(row);
                    else session.Update(old, row);
                }
            if (patch.Catalog is not null)
                session.Execute("UPDATE [app_metadata] SET [catalog_revision]=[catalog_revision]+1,[project_root]=?,[price_source]=?,[price_loaded_at]=? WHERE [singleton]=1",
                    (WorkflowMdbSession.TextColumn, nextCatalog.ProjectRoot), (WorkflowMdbSession.TextColumn, nextCatalog.PriceSource?.Path),
                    (WorkflowMdbSession.DateColumn, nextCatalog.PriceSource?.LoadedAt.UtcDateTime));
            var result = ReadResponse(session, ReadMetadata(session, request.DatabaseId), request with
            { Operation = WorkflowAccessOperation.ReadOrders, OrderIds = patch.Orders.Select(o => o.Id).ToArray() });
            // Read back and validate before commit. Returned timestamps are the values Jet actually stores.
            (result.Catalog ?? nextCatalog).WithOrders(result.Revision, result.Orders ?? []).Validate();
            commitAttempted = true; session.Commit(); return result;
        }
        catch (Exception e) when (commitAttempted && e is not OutOfMemoryException) { throw new WorkflowOutcomeUnknownException(); }
    }
}
