using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfficeOrderCreation(Guid Id, string Manager, string Customer, DateTimeOffset At,
    long? BaseRevision = null, bool NotSent = false)
{
    public bool SavedOffline { get; init; }
    public string? Problem { get; init; }
    public string? RetryMessage { get; init; }
}

internal sealed partial class WorkflowOfficeStorageClient
{
    private string QueuedOrderPath(Guid actor, Guid id) => Path.Combine(Path.GetDirectoryName(CreationPath(actor, null))!, $"queued-{id:N}.json");

    internal WorkflowOfficeOrderCreation[] ReadQueuedOrders(Guid actor)
    {
        var folder = Path.GetDirectoryName(CreationPath(actor, null))!;
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(folder);
        if (!Directory.Exists(folder)) return [];
        WorkflowServiceFiles.TryHide(OfficeCreationsRoot);
        var orders = new List<WorkflowOfficeOrderCreation>();
        foreach (var path in Directory.EnumerateFiles(folder, "queued-*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path).AsSpan(7), "N", out var id)) continue;
            try
            {
                if (ReadQueuedOrder(actor, id) is { } order) orders.Add(order);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
            {
                orders.Add(new(id, "", "Локальная заявка", default) { Problem = "Не удалось прочитать сохранённые реквизиты. Файл заявки оставлен на компьютере." });
            }
        }
        return orders.OrderBy(order => order.At).ToArray();
    }

    internal WorkflowOfficeOrderCreation? ReadQueuedOrder(Guid actor, Guid id)
    {
        var pending = ValidateOrderCreation(ReadCreation<WorkflowOfficeOrderCreation>(QueuedOrderPath(actor, id)));
        if (pending is not null && pending.Id != id) throw new WorkflowValidationException("ID отложенной заявки не совпадает с её файлом.");
        return pending;
    }

    internal FileStream LockQueuedOrders(Guid actor) =>
        LockCreationPath(Path.Combine(Path.GetDirectoryName(CreationPath(actor, null))!, "queue"));
    internal void SaveQueuedOrder(Guid actor, WorkflowOfficeOrderCreation pending) =>
        WriteLocalState(QueuedOrderPath(actor, pending.Id), JsonSerializer.SerializeToUtf8Bytes(pending, JsonOptions));
    internal void CompleteQueuedOrder(Guid actor, Guid id)
    {
        var path = QueuedOrderPath(actor, id);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
        File.Delete(path);
    }

    private string OfficeCreationsRoot => Path.Combine(profileDirectory, "office-creations");
    private string CreationPath(Guid actor, Guid? order) => Path.Combine(OfficeCreationsRoot,
        settings.DatabaseId.ToString("N"), actor.ToString("N"), order is { } id ? $"{id:N}.json" : "order.json");

    internal FileStream LockCreation(Guid actor, Guid? order = null) => LockCreationPath(CreationPath(actor, order));

    private FileStream LockCreationPath(string creationPath)
    {
        var path = creationPath + ".lock";
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WorkflowServiceFiles.TryHide(OfficeCreationsRoot);
        var lease = File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        WorkflowServiceFiles.TryHide(path);
        return lease;
    }

    internal WorkflowOfficeOrderCreation? ReadOrderCreation(Guid actor) => ValidateOrderCreation(ReadCreation<WorkflowOfficeOrderCreation>(actor, null));

    private static WorkflowOfficeOrderCreation? ValidateOrderCreation(WorkflowOfficeOrderCreation? pending)
    {
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

    private T? ReadCreation<T>(Guid actor, Guid? order) where T : class => ReadCreation<T>(CreationPath(actor, order));

    private static T? ReadCreation<T>(string path) where T : class
    {
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
