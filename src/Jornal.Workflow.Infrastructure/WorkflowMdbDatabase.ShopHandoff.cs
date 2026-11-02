using System.Runtime.Versioning;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed partial class WorkflowMdbDatabase
{
    private static WorkflowAccessResponse RecordShopHandoff(string path, WorkflowAccessRequest request)
    {
        var clear = request.Operation == WorkflowAccessOperation.ClearShopHandoff ? request.ClearShopHandoff
            ?? throw new WorkflowValidationException("Не передана снимаемая отметка.") : null;
        var partial = request.Operation == WorkflowAccessOperation.RecordProjectHandoffs ? request.ProjectHandoffs
            ?? throw new WorkflowValidationException("Не передан набор проектов.") : null;
        var mark = clear is null && partial is null ? request.ShopHandoff ?? throw new WorkflowValidationException("Не передана отметка передачи в цех.") : null;
        var orderId = clear?.OrderId ?? partial?.OrderId ?? mark!.OrderId;
        using var session = new WorkflowMdbSession(path, true);
        var original = ReadMetadata(session, request.DatabaseId);
        session.Begin(); var commitAttempted = false;
        try
        {
            if (session.Execute("UPDATE [app_metadata] SET [revision]=[revision]+1 WHERE [singleton]=1 AND [database_id]=?",
                (WorkflowMdbSession.GuidColumn, original.DatabaseId)) != 1) throw new WorkflowConflictException();
            var meta = ReadMetadata(session, original.DatabaseId);
            var data = ReadOrders(session, CheckedIds([orderId]));
            var order = WorkflowMdbMapping.Orders(data);
            var catalog = ReadCatalog(session, meta);
            var book = catalog.WithOrders(meta.Revision, order);
            var next = clear is not null ? WorkflowChanges.ClearShopHandoff(book, orderId, clear.ExpectedOrderVersion, clear.Expected, clear.ActorId)
                : partial is not null ? WorkflowChanges.RecordProjectHandoffs(book, orderId, partial.ExpectedOrderVersion, partial.ProjectIds, partial.ActorId, partial.At)
                : WorkflowChanges.RecordShopHandoff(book, orderId, mark!.ExpectedOrderVersion, mark.ActorId, mark.At);
            if (ReferenceEquals(book, next))
            {
                var unchanged = ReadResponse(session, meta, request with { Operation = WorkflowAccessOperation.ReadOrders, OrderIds = [orderId] });
                session.Rollback(); return unchanged with { Revision = original.Revision };
            }
            var nextOrder = next.Orders.Single();
            (string Table, WorkflowMdbRow[] Rows)[] changes =
            [
                ("order_shop_handoffs", nextOrder.ShopHandoff is { } stamp ? [WorkflowMdbMapping.HandoffRow(orderId, stamp)] : []),
                ("project_shop_handoffs", nextOrder.Projects.Where(p => p.ShopHandoff is not null).Select(p => WorkflowMdbMapping.HandoffRow(p.Id, p.ShopHandoff!, "project_shop_handoffs")).ToArray()),
                ("project_shop_handoff_inputs", nextOrder.Projects.Where(p => p.ShopHandoff?.ProjectInputs is not null).Select(p => WorkflowMdbMapping.HandoffInputsRow(p.Id, p.ShopHandoff!.ProjectInputs!)).ToArray())
            ];
            // Children must be removed before their marks, then inserted after them.
            foreach (var (table, after) in changes.Reverse())
            {
                var ids = after.Select(r => r.Id).ToHashSet();
                foreach (var old in data[table].Values.Where(r => !ids.Contains(r.Id)))
                    session.Execute($"DELETE FROM [{table}] WHERE [id]=?", (WorkflowMdbSession.GuidColumn, old.Id));
            }
            foreach (var (table, after) in changes)
            {
                var before = data[table];
                foreach (var row in after)
                    if (!before.TryGetValue(row.Id, out var previous)) session.Insert(row);
                    else if (!WorkflowMdbMapping.Same(previous, row)) session.Update(previous, row);
            }
            var result = ReadResponse(session, meta, request with { Operation = WorkflowAccessOperation.ReadOrders, OrderIds = [orderId] });
            catalog.WithOrders(result.Revision, result.Orders ?? []).Validate();
            commitAttempted = true; session.Commit(); return result;
        }
        catch (Exception ex) when (commitAttempted && ex is not OutOfMemoryException) { throw new WorkflowOutcomeUnknownException(ex); }
    }
}
