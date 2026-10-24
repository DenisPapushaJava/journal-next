using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    public async Task<WorkflowReportReviewViewModel?> CreateReportReviewAsync(CancellationToken token = default, WorkflowReportReviewViewModel? previous = null)
    {
        if (!CanUse || CurrentOrder is not { IsDeleted: false } order) return null;
        if (HasCurrentCardEdits) { Error = "Сохраните или отмените текущие изменения перед подбором отчётов."; return null; }
        if (order.Projects.Length == 0) { Error = "Сначала подготовьте проекты в заявке."; return null; }
        WorkflowReportReviewViewModel? review = null;
        await RunAsync(async () =>
        {
            var latest = await _store.ReadAsync(token); var latestOrder = latest.Orders.Single(o => o.Id == order.Id);
            if (previous is not null && latestOrder.Version != previous.PreviewOrderVersion)
                throw new WorkflowValidationException("Заявка изменена в другом окне. Текущий подбор сохранён; для новой проверки закройте сводку и обновите заявку через F5.");
            _workspace = latest; order = latestOrder; RefreshOrders(); LoadOrder(order);
            var snapshot = _workspace; var actor = ActorId();
            review = new((candidates, cancellation) => ApplyReportReviewAsync(order, actor, candidates, cancellation),
                async (prior, cancellation) =>
                {
                    var renewed = await CreateReportReviewAsync(cancellation, prior);
                    return (renewed, renewed is null ? Error : null);
                }) { PreviewOrderVersion = order.Version };
            var scopes = await Task.Run(async () =>
            {
                var result = new List<(CuttingProject Project, ReportFileSnapshot[] Files, string State)>();
                var folders = new Dictionary<string, (ReportFileSnapshot[] Files, string State)>(
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                long totalBytes = 0;
                foreach (var project in order.Projects.Where(p => order.Items.Any(i => i.CurrentProjectId == p.Id) ||
                    p.Variant == order.Projects.Where(other => other.ItemId == p.ItemId).Max(other => other.Variant)))
                {
                    token.ThrowIfCancellationRequested();
                    var existing = order.Specifications.SingleOrDefault(s => s.Id == project.Id);
                    var folder = project.NativePath is { } native ? Path.GetDirectoryName(native) :
                        existing?.Current.SourcePath is { } source ? Path.GetDirectoryName(source) : null;
                    if (folder is null) { result.Add((project, [], "Папка проекта не задана")); continue; }
                    if (!folders.TryGetValue(folder, out var scanned))
                    {
                        var files = new List<ReportFileSnapshot>(); var state = "Отчётов пока нет";
                        try
                        {
                            var paths = Directory.EnumerateFiles(folder).Where(WorkflowReportFiles.IsReport).Take(101).ToArray();
                            if (paths.Length > 100) state = "В папке больше 100 отчётов. Уточните папку проекта.";
                            else foreach (var path in paths)
                            {
                                token.ThrowIfCancellationRequested();
                                try
                                {
                                    if (totalBytes + new FileInfo(path).Length > 128 * 1024 * 1024)
                                    { state = "Подбор больше 128 МБ. Обновляйте проекты меньшими наборами."; break; }
                                    var file = await WorkflowReportFiles.ReadAsync(path, token);
                                    files.Add(file); totalBytes += file.Bytes.LongLength;
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                                { state = "Часть отчётов недоступна или не распознана. Повторите после сохранения в CAM."; }
                            }
                            if (files.Count > 0 && state == "Отчётов пока нет") state = "Не найден отчёт с именем этого проекта в заголовке";
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { state = "Папка проекта недоступна"; }
                        scanned = (files.ToArray(), state); folders.Add(folder, scanned);
                    }
                    var matched = scanned.Files.Where(file => WorkflowReportFiles.Matches(file, project, existing,
                            snapshot.Machines.Single(m => m.Id == project.MachineId)))
                        .GroupBy(file => file.Sha256).Select(group => group.First()).ToArray();
                    result.Add((project, matched, scanned.State));
                }
                return result;
            }, token);
            foreach (var scope in scopes)
                review.Candidates.Add(new(snapshot, scope.Project, order.Items.Single(i => i.Id == scope.Project.ItemId).Number,
                    scope.Files, scope.State, review.Changed, previous?.Candidates.SingleOrDefault(c => c.Project.Id == scope.Project.Id)));
            review.Selected = review.Candidates.FirstOrDefault(c => c.Draft is not null) ?? review.Candidates.FirstOrDefault();
            review.Changed();
        });
        return Error is null ? review : null;
    }

    private async Task<string?> ApplyReportReviewAsync(WorkflowOrder previewOrder, Guid actor,
        IReadOnlyList<WorkflowReportCandidate> candidates, CancellationToken token)
    {
        var saved = false;
        await RunAsync(async () =>
        {
            if (CurrentOrder?.Id != previewOrder.Id) throw new WorkflowConflictException();
            var changes = candidates.Select(candidate =>
            {
                if (!candidate.Ready || candidate.Snapshot is null || candidate.Baseline is null || candidate.Draft is not { } draft)
                    throw new WorkflowValidationException("Проверьте выбранные отчёты.");
                return (candidate.Snapshot, candidate.Baseline, Draft: draft, Parameters: draft.Parameters(),
                    Rate: draft.Rate(), Date: draft.DocumentDate(), Number: draft.Number);
            }).ToArray();
            if (changes.Length == 0) throw new WorkflowValidationException("Нет готовых отчётов для сохранения.");
            var accepted = new List<(SpecificationDraft Draft, CuttingParameters Parameters, CuttingRate Rate,
                DateOnly Date, string Number, AcceptedReport Report)>();
            foreach (var change in changes)
            {
                await Task.Run(() => WorkflowReportFiles.RevalidateAsync(change.Snapshot, token), token);
                var prior = change.Draft.Saved?.Report;
                var report = prior?.Sha256 == change.Snapshot.Sha256 ? prior :
                    await Task.Run(() => WorkflowReportFiles.ArchiveAsync(change.Snapshot, change.Baseline, token), token);
                accepted.Add((change.Draft, change.Parameters, change.Rate, change.Date, change.Number, report));
            }
            _workspace = await _store.WriteOrderAsync(previewOrder.Id, previewOrder.Version, book =>
            {
                var next = book;
                foreach (var entry in accepted)
                    next = WorkflowChanges.SaveSpecification(next, previewOrder.Id, next.Orders.Single(o => o.Id == previewOrder.Id).Version,
                        entry.Draft.Id, entry.Parameters, entry.Number, entry.Date, actor, DateTimeOffset.UtcNow,
                        entry.Draft.SourcePath, entry.Draft.ImportProfile, entry.Rate, entry.Report);
                return next;
            }, token);
            saved = true; RefreshOrders(); LoadOrder(_workspace.Orders.Single(o => o.Id == previewOrder.Id));
        });
        if (!saved && Error is null) token.ThrowIfCancellationRequested();
        return saved ? null : Error ?? "Отчёты не сохранены; подбор остался в окне.";
    }
}
