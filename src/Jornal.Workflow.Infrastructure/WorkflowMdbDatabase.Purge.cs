using System.Globalization;
using System.Runtime.Versioning;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed partial class WorkflowMdbDatabase
{
    private static WorkflowAccessResponse Purge(string path, WorkflowAccessRequest request)
    {
        var targets = request.TrashTargets ?? [];
        if (targets.Length is < 1 or > 10000 || targets.Any(t => t.OrderId == Guid.Empty || t.Version < 0) ||
            targets.Select(t => t.OrderId).Distinct().Count() != targets.Length || request.ActorId is null || request.ActorId == Guid.Empty)
            throw new WorkflowValidationException("Проверьте набор удаляемых заявок (до 10 000 за одну операцию).");
        using var session = new WorkflowMdbSession(path, true);
        var original = ReadMetadata(session, request.DatabaseId);
        session.Begin(); var commitAttempted = false;
        try
        {
            if (session.Execute("UPDATE [app_metadata] SET [revision]=[revision]+1 WHERE [singleton]=1 AND [database_id]=?",
                (WorkflowMdbSession.GuidColumn, original.DatabaseId)) != 1) throw new WorkflowConflictException();
            if (Convert.ToInt32(session.Scalar("SELECT COUNT(*) FROM [employees] WHERE [id]=? AND [is_technologist]<>0 AND [is_archived]=0",
                (WorkflowMdbSession.GuidColumn, request.ActorId)), CultureInfo.InvariantCulture) != 1)
                throw new WorkflowValidationException("Выберите действующего технолога.");
            // Validate the complete frozen set first. Never fetch or retransmit calculation/report bodies to delete them.
            foreach (var batch in targets.Chunk(75))
            {
                var args = batch.Select(t => (WorkflowMdbSession.GuidColumn, (object?)t.OrderId)).ToArray();
                var ids = string.Join(",", batch.Select(_ => "?"));
                var rows = session.Read("orders", "[id] IN (" + ids + ")", args);
                foreach (var target in batch)
                    if (!rows.TryGetValue(target.OrderId, out var row) || row.Values["deleted_at"] is null ||
                        Convert.ToInt64(row.Values["row_version"], CultureInfo.InvariantCulture) != target.Version) throw new WorkflowConflictException();
            }
            foreach (var batch in targets.Chunk(75))
            {
                var args = batch.Select(t => (WorkflowMdbSession.GuidColumn, (object?)t.OrderId)).ToArray();
                var ids = string.Join(",", batch.Select(_ => "?"));
                void Delete(string sql) => session.Execute(sql, args);
                Delete("DELETE FROM [accepted_reports] WHERE [specification_id] IN (SELECT [id] FROM [specifications] WHERE [order_id] IN (" + ids + "))");
                Delete("DELETE FROM [project_renames] WHERE [project_id] IN (SELECT [id] FROM [cutting_projects] WHERE [order_id] IN (" + ids + "))");
                Delete("DELETE FROM [order_item_selections] WHERE [order_id] IN (" + ids + ")");
                foreach (var table in new[] { "specifications", "cutting_projects", "order_items" })
                    Delete($"DELETE FROM [{table}] WHERE [order_id] IN (" + ids + ")");
                if (session.Execute("DELETE FROM [orders] WHERE [id] IN (" + ids + ")", args) != batch.Length) throw new WorkflowConflictException();
            }
            var result = ReadResponse(session, ReadMetadata(session, original.DatabaseId), request with
            { Operation = WorkflowAccessOperation.ReadOrders, OrderIds = [] });
            commitAttempted = true; session.Commit(); return result;
        }
        catch (Exception ex) when (commitAttempted && ex is not OutOfMemoryException) { throw new WorkflowOutcomeUnknownException(); }
    }
}
