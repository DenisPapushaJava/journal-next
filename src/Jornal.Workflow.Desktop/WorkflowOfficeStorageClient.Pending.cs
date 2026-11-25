using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowOfficePendingFiles(Guid[] Projects, Guid[] Unavailable, bool IndexUnavailable = false)
{
    public bool Incomplete => IndexUnavailable || Unavailable.Length > 0;
}

internal sealed record WorkflowOfficeFileBatch(int Completed, int Sent, string[] Problems);

internal sealed partial class WorkflowOfficeStorageClient
{
    internal async Task<WorkflowOfficePendingFiles> ScanPendingFilesAsync(CancellationToken token, IReadOnlySet<Guid>? scope = null)
    {
        Guid[] copies;
        try { copies = FindLocalCopies(token); }
        catch (Exception ex) when (IsCopyUnavailable(ex)) { return new([], [], IndexUnavailable: true); }

        var pending = new List<Guid>();
        var unavailable = new List<Guid>();
        foreach (var project in copies)
        {
            token.ThrowIfCancellationRequested();
            if (scope is not null && !scope.Contains(project)) continue;
            try
            {
                switch (await HasPendingFilesAsync(project, null, null, token, indexedCopy: true))
                {
                    case true: pending.Add(project); break;
                    case null: unavailable.Add(project); break;
                }
            }
            catch (Exception ex) when (IsCopyUnavailable(ex)) { unavailable.Add(project); }
        }
        return new(pending.ToArray(), unavailable.ToArray());
    }

    private Guid[] FindLocalCopies(CancellationToken token)
    {
        var copies = new HashSet<Guid>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(CopyIndexFolder))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                if (name.EndsWith(".location", StringComparison.Ordinal))
                    name = Path.GetFileNameWithoutExtension(name);
                else if (name.EndsWith(CleanupMoveSuffix, StringComparison.Ordinal))
                    name = name[..^CleanupMoveSuffix.Length];
                if (Guid.TryParseExact(name, "N", out var id) && id != Guid.Empty)
                    copies.Add(id);
            }
        }
        catch (DirectoryNotFoundException) { return []; }
        return copies.Order().ToArray();
    }

    internal async Task<WorkflowOfficeFileBatch> PublishPendingFilesAsync(Guid[] projects, WorkflowOrder[] visibleOrders,
        IProgress<int> progress, CancellationToken token)
    {
        if (!settings.CanWrite) throw new WorkflowValidationException("На сервере выключено удалённое сохранение.");
        var missing = projects.ToHashSet();
        var targets = new Dictionary<Guid, (WorkflowOrder Order, CuttingProject Project)>();
        var problems = new List<string>();
        var failedLookup = false;
        try { await FindOfficeProjectsAsync(missing, visibleOrders, targets, token); }
        catch (WorkflowOfficeSyncUnavailableException ex) { return new(0, 0, [ex.Message]); }
        catch (Exception ex) when (IsCopyUnavailable(ex) || ex is WorkflowConflictException) { failedLookup = true; }

        var completed = 0;
        var sent = 0;
        var checkedProjects = 0;
        foreach (var project in projects)
        {
            token.ThrowIfCancellationRequested();
            if (targets.TryGetValue(project, out var target))
            {
                try
                {
                    var publication = await PublishCopyAsync(target.Order.Id, project, token,
                        reportName: WorkflowOfficeProjectPaths.AcceptedReportName(target.Order, project), verifyProject: target.Project);
                    sent += publication.Sent;
                    problems.AddRange(publication.Problems.Select(message => $"{target.Project.Name} — {message}"));
                    if (publication.StopReason is { } stop)
                    {
                        problems.Add(stop);
                        return new(completed, sent, problems.ToArray());
                    }
                    if (publication.Problems.Length == 0) completed++;
                }
                catch (Exception ex) when (IsCopyUnavailable(ex) || ex is WorkflowConflictException or HttpRequestException ||
                    ex is OperationCanceledException && !token.IsCancellationRequested)
                {
                    var message = ex is WorkflowValidationException ? ex.Message : "Не удалось подтвердить отправку. Проверьте связь и закройте файлы проекта.";
                    problems.Add($"{target.Project.Name} — {message}");
                }
            }
            progress.Report(++checkedProjects);
        }
        if (missing.Count > 0)
            problems.Add(failedLookup
                ? $"Проектов, для которых не удалось проверить офисную заявку: {missing.Count}. Проверьте связь и повторите отправку."
                : $"Проектов, которых нет в активных заявках сервера: {missing.Count}. Проверьте корзину заявок и проектов.");
        return new(completed, sent, problems.ToArray());
    }

    private async Task FindOfficeProjectsAsync(HashSet<Guid> missing, WorkflowOrder[] visibleOrders,
        Dictionary<Guid, (WorkflowOrder Order, CuttingProject Project)> targets, CancellationToken token)
    {
        void FindTargets(IEnumerable<WorkflowOrder> orders)
        {
            foreach (var order in orders.Where(o => !o.IsDeleted))
                foreach (var project in order.ActiveProjects)
                    if (missing.Remove(project.Id))
                        targets.Add(project.Id, (order, project));
        }

        var query = new WorkflowReadQuery();
        long? catalogVersion = null;
        // The visible list may predate edits made on another computer.
        var knownOrders = visibleOrders.Where(order => !order.IsDeleted && order.ActiveProjects.Any(project => missing.Contains(project.Id)))
            .Select(order => order.Id);
        foreach (var ids in knownOrders.Distinct().Chunk(query.Limit))
        {
            var current = await SendStorageAsync(new(1, WorkflowAccessOperation.ReadOrders, "", OrderIds: ids,
                KnownCatalogVersion: catalogVersion), token, workingFiles: true);
            catalogVersion = current.CatalogVersion;
            FindTargets(current.Orders!);
        }
        // Old copies only store the project ID. Search beyond the current date and filters.
        while (missing.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var page = await SendStorageAsync(new(1, WorkflowAccessOperation.Read, "", Query: query, KnownCatalogVersion: catalogVersion), token, workingFiles: true);
            catalogVersion = page.CatalogVersion;
            FindTargets(page.Orders!);
            if (!page.HasMore || missing.Count == 0) break;
            var lastId = page.PageIds?.LastOrDefault();
            var last = page.Orders!.SingleOrDefault(o => o.Id == lastId);
            if (last is null || query.BeforeUpdated is { } before &&
                (last.UpdatedAt > before || last.UpdatedAt == before && last.Number >= query.BeforeNumber))
                throw new InvalidDataException("Сервер не вернул следующую страницу заявок.");
            query = query with { BeforeUpdated = last.UpdatedAt, BeforeNumber = last.Number };
        }
    }

    private static bool IsCopyUnavailable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or WorkflowValidationException or InvalidDataException or JsonException;

    internal async Task<WorkflowOrder> ReadActiveOrderForWorkingFilesAsync(Guid order, CancellationToken token)
    {
        var snapshot = await SendStorageAsync(new(1, WorkflowAccessOperation.ReadOrders, "", OrderIds: [order]), token, workingFiles: true);
        return snapshot.Orders!.SingleOrDefault(candidate => candidate.Id == order && !candidate.IsDeleted)
            ?? throw new WorkflowValidationException("Заявка недоступна на сервере. Обновите журнал и проверьте корзину.");
    }
}
