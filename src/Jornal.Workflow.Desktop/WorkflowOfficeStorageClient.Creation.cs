using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfficeOrderCreation(Guid Id, string Manager, string Customer, DateTimeOffset At,
    long? BaseRevision = null, bool NotSent = false);

internal sealed partial class WorkflowOfficeStorageClient
{
    private string CreationPath(Guid actor, Guid? order) => Path.Combine(profileDirectory, "office-creations",
        settings.DatabaseId.ToString("N"), actor.ToString("N"), order is { } id ? $"{id:N}.json" : "order.json");

    internal FileStream LockCreation(Guid actor, Guid? order = null)
    {
        var path = CreationPath(actor, order) + ".lock";
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lease = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        WorkflowServiceFiles.TryHide(path);
        return lease;
    }

    internal WorkflowOfficeOrderCreation? ReadOrderCreation(Guid actor)
    {
        var pending = ReadCreation<WorkflowOfficeOrderCreation>(actor, null);
        if (pending is not null && (pending.Id == Guid.Empty || pending.At == default || pending.BaseRevision is < 0 ||
            pending.NotSent && pending.BaseRevision is not null ||
            string.IsNullOrWhiteSpace(pending.Manager) || string.IsNullOrWhiteSpace(pending.Customer)))
            throw new WorkflowValidationException("Не удалось прочитать сохранённое создание заявки.");
        return pending;
    }

    internal WorkflowOfficeProjectPreparation? ReadProjectCreation(Guid actor, Guid order)
    {
        var pending = ReadCreation<WorkflowOfficeProjectPreparation>(actor, order);
        if (pending is not null && (pending.ProjectId == Guid.Empty || pending.ActorId != actor ||
            pending.MachineId == Guid.Empty || pending.ThicknessMm <= 0 || pending.OrderVersion <= 0))
            throw new WorkflowValidationException("Не удалось прочитать сохранённое создание проекта.");
        return pending;
    }

    private T? ReadCreation<T>(Guid actor, Guid? order) where T : class
    {
        var path = CreationPath(actor, order);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
        try
        {
            using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(input, JsonOptions) ?? throw new JsonException();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (JsonException) { throw new WorkflowValidationException("Не удалось прочитать локальные данные незавершённого создания."); }
    }

    internal void SaveCreation<T>(Guid actor, Guid? order, T pending) =>
        WriteLocalState(CreationPath(actor, order), JsonSerializer.SerializeToUtf8Bytes(pending, JsonOptions));

    // Call while holding the creation lock, after finding the saved ID in the office.
    internal void CompleteCreation(Guid actor, Guid? order = null)
    {
        var path = CreationPath(actor, order);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
        File.Delete(path);
    }
}
