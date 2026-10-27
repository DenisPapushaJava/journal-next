using System.Runtime.Versioning;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

[SupportedOSPlatform("windows")]
public sealed partial class WorkflowMdbDatabase
{
    private static void UpgradeSchema(string path, Guid? expected)
    {
        using (var read = new WorkflowMdbSession(path, false))
            if (ReadMetadata(read, expected, allowVersionOne: true).SchemaVersion == WorkflowMdbSchema.Version) return;
        var stage = Path.Combine(Path.GetDirectoryName(path)!, ".Workflow-upgrade-" + Guid.NewGuid().ToString("N") + ".mdb");
        var backup = path + ".schema-v1-" + Guid.NewGuid().ToString("N") + ".bak";
        try
        {
            // Deny all readers/writers while preparing an independent copy. Delete sharing permits
            // our final atomic replacement without reopening an unprotected source file.
            using var original = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new WorkflowValidationException("Для обновления MDB выберите сам файл базы, а не ссылку на него.");
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { original.CopyTo(output); output.Flush(true); }
            WorkflowServiceFiles.TryHide(stage);
            using (var copy = new WorkflowMdbSession(stage, true))
            {
                var metadata = ReadMetadata(copy, expected, allowVersionOne: true);
                if (metadata.SchemaVersion == WorkflowMdbSchema.Version) return;
                foreach (var sql in WorkflowMdbSchema.CreateSql().Where(sql => sql.Contains("[order_shop_handoffs]", StringComparison.Ordinal))) copy.Execute(sql);
                ValidateSchema(copy);
                copy.Execute($"UPDATE [app_metadata] SET [schema_version]={WorkflowMdbSchema.Version},[revision]=[revision]+1 WHERE [singleton]=1");
                ReadMetadata(copy, metadata.DatabaseId);
            }
            File.SetAttributes(stage, File.GetAttributes(path));
            File.Replace(stage, path, backup);
            WorkflowServiceFiles.TryHide(backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Data.OleDb.OleDbException or NotSupportedException)
        { throw new WorkflowValidationException("Не удалось обновить структуру MDB. Закройте подключения к этой базе на других компьютерах, проверьте доступ к папке и повторите подключение. Обновление выполняется через отдельную копию; исходные заявки не переписываются."); }
        finally
        {
            if (File.Exists(stage))
                try { File.Delete(stage); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
