using System.Runtime.Versioning;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed partial class WorkflowMdbDatabase
{
    private static WorkflowAccessResponse RecordShopHandoff(string path, WorkflowAccessRequest request)
    {
        var mark = request.ShopHandoff ?? throw new WorkflowValidationException("Не передана отметка передачи в цех.");
        using var session = new WorkflowMdbSession(path, true);
        var original = ReadMetadata(session, request.DatabaseId);
        session.Begin(); var commitAttempted = false;
        try
        {
            if (session.Execute("UPDATE [app_metadata] SET [revision]=[revision]+1 WHERE [singleton]=1 AND [database_id]=?",
                (WorkflowMdbSession.GuidColumn, original.DatabaseId)) != 1) throw new WorkflowConflictException();
            var meta = ReadMetadata(session, original.DatabaseId);
            var data = ReadOrders(session, CheckedIds([mark.OrderId]));
            var order = WorkflowMdbMapping.Orders(data);
            var catalog = ReadCatalog(session, meta);
            var book = catalog.WithOrders(meta.Revision, order);
            var next = WorkflowChanges.RecordShopHandoff(book, mark.OrderId, mark.ExpectedOrderVersion, mark.ActorId, mark.At);
            if (ReferenceEquals(book, next))
            {
                var unchanged = ReadResponse(session, meta, request with { Operation = WorkflowAccessOperation.ReadOrders, OrderIds = [mark.OrderId] });
                session.Rollback(); return unchanged with { Revision = original.Revision };
            }
            var before = data["order_shop_handoffs"];
            var row = WorkflowMdbMapping.HandoffRow(mark.OrderId, next.Orders.Single().ShopHandoff!);
            if (before.TryGetValue(row.Id, out var previous)) session.Update(previous, row); else session.Insert(row);
            var result = ReadResponse(session, meta, request with { Operation = WorkflowAccessOperation.ReadOrders, OrderIds = [mark.OrderId] });
            catalog.WithOrders(result.Revision, result.Orders ?? []).Validate();
            commitAttempted = true; session.Commit(); return result;
        }
        catch (Exception ex) when (commitAttempted && ex is not OutOfMemoryException) { throw new WorkflowOutcomeUnknownException(ex); }
    }
}
