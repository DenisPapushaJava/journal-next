using Jornal.Parsing.Documents;
using Jornal.Parsing.Specifications;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    // Keep one bounded scan in memory so a partial acceptance or a trip to prices
    // does not discard the remaining choices/conflicts. F5 explicitly starts over.
    private (Guid OrderId, WorkflowReportReviewViewModel Review)? _reportReview;
    private Guid? _reportImportOrderId;
    private int _acceptedReports;
    private bool HasPendingReportEdits => _reportReview?.Review.Candidates.Any(c => !c.WasAccepted && c.Draft?.IsDirty == true) == true;
    // A newer order version must not hide its retained review draft.
    private WorkflowReportReviewViewModel? CurrentReportReview => _reportReview is { } session &&
        CurrentOrder is { } order && session.OrderId == order.Id ? session.Review : null;
    public bool HasReportImportNotice => CurrentOrder is { } order && _reportImportOrderId == order.Id && _acceptedReports > 0;
    public string AcceptedReportsCaption => $"Обновлено {_acceptedReports}";
    public string ReportImportNotice => !HasReportImportNotice ? "" : $"Обновлено {_acceptedReports}" +
        (WaitingProjectCount > 0 ? $", ещё {WaitingProjectCount} ожидают спецификацию" : "") +
        (ReviewProjectCount > 0 ? $", требуют проверки: {ReviewProjectCount}" : "");
    internal void ReportReviewClosed() { ++_closeRevision; NotifyCompleteness(); NotifyNavigation(); }

    public async Task<WorkflowReportReviewViewModel?> CreateReportReviewAsync(CancellationToken token = default, WorkflowReportReviewViewModel? previous = null, bool calculation = false)
    {
        if (!CanUse || !IsCurrentOrderActive || CurrentOrder is not { IsDeleted: false } order) return null;
        if (calculation ? !CheckCalculationInputs() : HasCurrentCardEdits) { Error = "Сохраните или отмените текущие изменения перед подбором отчётов."; return null; }
        if (order.Projects.Length == 0) { Error = "Сначала подготовьте проекты в заявке."; return null; }
        if (order.HasProjectRename) { Error = "Завершите изменение параметров проекта перед подбором отчётов."; return null; }
        if (!calculation && !CheckPendingCalculationReview()) { Error = CalculationNotice; return null; }
        previous ??= CurrentReportReview;
        var source = _store; var requestedOrderId = order.Id;
        var localDrafts = Drafts.Where(d => d.IsDirty).ToDictionary(d => d.Id);
        WorkflowReportReviewViewModel? review = null;
        await RunAsync(async () =>
        {
            var latest = await source.ReadOrderAsync(order.Id, token); var latestOrder = latest.Orders.Single(o => o.Id == order.Id);
            if (!calculation && previous is not null && latestOrder.Version != previous.PreviewOrderVersion)
            {
                // Reopen the original draft for inspection; never rebase it silently.
                previous.MarkOrderChanged(); review = previous; return;
            }
            if (!ReferenceEquals(source, _store) || CurrentOrder?.Id != order.Id) throw new WorkflowConflictException();
            if (!calculation) RequireUnchangedReportDrafts(latestOrder);
            var actorForHistory = ActorId();
            if (calculation)
            {
                foreach (var local in localDrafts.Values.Where(d => CalculationProjectIds(latestOrder).Contains(d.Id)))
                    await Task.Run(() => WorkflowCalculationSnapshots.PreserveAsync(order, local.Id, actorForHistory, local.Saved, local, local.SourcePath, token), token);
                if (previous is not null)
                    foreach (var candidate in previous.Candidates.Where(c => !c.WasAccepted && c.HasUnpreservedEdits && c.Draft is not null))
                        await Task.Run(() => WorkflowCalculationSnapshots.PreserveAsync(order, candidate.Project.Id, actorForHistory, candidate.Draft!.Saved, candidate.Draft, candidate.Snapshot?.SourcePath, token), token);
            }
            if (calculation && _reportReview is { } other && other.OrderId != order.Id)
            {
                foreach (var candidate in other.Review.Candidates.Where(c => !c.WasAccepted && c.HasUnpreservedEdits && c.Draft is not null))
                    await Task.Run(() => WorkflowCalculationSnapshots.PreserveAsync(
                        Path.GetDirectoryName(candidate.Project.NativePath ?? candidate.Draft!.SourcePath), other.OrderId, candidate.Project.Id,
                        actorForHistory, candidate.Draft!.Saved, candidate.Draft, token, other.Review.PreviewOrderVersion), token);
            }
            _workspace = latest; order = latestOrder; RefreshOrders(preserveRows: true); LoadReportOrder(order);
            if (order.IsDeleted || order.HasProjectRename) throw new WorkflowValidationException("Подбор недоступен: заявка в корзине или изменение проекта не завершено.");
            var snapshot = _workspace; var actor = ActorId();
            review = new(async (candidates, cancellation) =>
                {
                    if (calculation) CanRetryCalculation = false;
                    var error = await ApplyReportReviewAsync(order, actor, candidates, source, cancellation, calculation);
                    if (error is not null && calculation && CanRetryCalculation)
                    {
                        review!.MarkOrderChanged();
                        error = "Заявка сейчас обновляется. Нажмите «Продолжить» — введённые данные сохранены.";
                    }
                    if (error is null)
                    {
                        order = CurrentOrder!; review!.PreviewOrderVersion = order.Version;
                        foreach (var candidate in candidates) candidate.MarkAccepted();
                        if (review.Candidates.All(candidate => !candidate.NeedsAttention)) _reportReview = null;
                        _reportImportOrderId = order.Id; _acceptedReports = candidates.Count;
                        NotifyCompleteness();
                    }
                    return error;
                },
                async (prior, cancellation) =>
                {
                    var renewed = await CreateReportReviewAsync(cancellation, prior, calculation);
                    return (renewed, renewed is null ? Error : null);
                }, () => _workspace) { MaterialSuggestions = MaterialSuggestions, PreviewOrderVersion = order.Version, CurrentOrderVersion = () => CurrentOrder?.Version, IsContextCurrent = () => ReferenceEquals(source, _store) && CurrentOrder?.Id == requestedOrderId && !_cuttingTaskDetached };
            var scopes = await Task.Run(async () =>
            {
                var result = new List<(CuttingProject Project, ReportFileSnapshot[] Files, string State)>();
                var profiles = new Dictionary<string, Dictionary<string, (ReportFileSnapshot[] Files, string State)>>(StringComparer.Ordinal);
                long totalBytes = 0;
                foreach (var project in order.ActiveProjects.Where(p => order.Items.Any(i => i.Id == p.ItemId && i.IsIncluded) &&
                    (order.Items.Any(i => i.CurrentProjectId == p.Id) || !calculation &&
                    p.Variant == order.ActiveProjects.Where(other => other.ItemId == p.ItemId).Max(other => other.Variant))))
                {
                    token.ThrowIfCancellationRequested();
                    var existing = order.Specifications.SingleOrDefault(s => s.Id == project.Id);
                    var folder = project.NativePath is { } native ? Path.GetDirectoryName(native) :
                        existing?.Current.SourcePath is { } reportSource ? Path.GetDirectoryName(reportSource) : null;
                    if (folder is null) { result.Add((project, [], "Папка проекта не задана")); continue; }
                    if (calculation && existing is null && !localDrafts.ContainsKey(project.Id) && previous?.Candidates.SingleOrDefault(c => c.Project.Id == project.Id)?.Draft is null)
                    {
                        var history = await WorkflowCalculationHistory.ReadAsync(folder, order.Id, project.Id, token);
                        if (history.Warning is not null) throw new WorkflowValidationException("Не удалось прочитать прежние ручные настройки проекта. Проверьте историю в папке проекта.");
                        if (history.Snapshots.Where(s => s.State is null && s.Draft is not null).OrderByDescending(s => s.CapturedAt).FirstOrDefault()?.Draft is { } pending)
                            localDrafts[project.Id] = new SpecificationDraft { Id = project.Id, Coefficient = pending.Coefficient,
                                HasBending = pending.HasBending, HasChamfer = pending.HasChamfer, Comment = pending.Comment };
                    }
                    // A shared folder can contain projects with different specification formats.
                    if (!profiles.TryGetValue(project.ImportProfile ?? "", out var folders))
                    {
                        folders = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                        profiles.Add(project.ImportProfile ?? "", folders);
                    }
                    if (!folders.TryGetValue(folder, out var scanned))
                    {
                        var files = new List<ReportFileSnapshot>(); var state = "Отчётов пока нет";
                        try
                        {
                            var paths = Directory.EnumerateFiles(folder).Where(path => WorkflowReportFiles.IsReport(path, project.ImportProfile)).Take(101).ToArray();
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
                                catch (DocumentRejectedException ex) when (project.ImportProfile == WaterjetPdfSpecificationParser.Profile &&
                                    ex.Reason == DocumentRejection.UnrecognizedSpecification &&
                                    !Path.GetFileNameWithoutExtension(path).EndsWith("_ce", StringComparison.OrdinalIgnoreCase))
                                { /* A readable drawing without a specification layout is not a candidate. */ }
                                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                                { state = $"Часть отчётов недоступна или не распознана: «{Path.GetFileName(path)}». Закройте файл или завершите сохранение в CAM и повторите расчёт."; }
                            }
                            if (files.Count > 0 && state == "Отчётов пока нет") state = "Не найден отчёт с именем этого проекта в заголовке";
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { state = "Папка проекта недоступна"; }
                        scanned = (files.ToArray(), state); folders.Add(folder, scanned);
                    }
                    var preferred = existing?.Current.SourcePath ?? previous?.Candidates.SingleOrDefault(c => c.Project.Id == project.Id)?.Snapshot?.SourcePath;
                    var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                    var matched = scanned.Files.Where(file => WorkflowReportFiles.Matches(file, project, existing,
                            snapshot.Machines.Single(m => m.Id == project.MachineId)))
                        .OrderByDescending(file => preferred is not null && pathComparer.Equals(preferred, file.SourcePath))
                        .GroupBy(file => file.Sha256).Select(group => group.First()).ToArray();
                    if (calculation && preferred is not null && matched.FirstOrDefault(file => pathComparer.Equals(preferred, file.SourcePath)) is { } bound)
                        result.Add((project, [bound], ""));
                    else result.Add((project, matched, scanned.State));
                }
                return result;
            }, token);
            foreach (var scope in scopes)
                review.Candidates.Add(new(snapshot, scope.Project, order.Items.Single(i => i.Id == scope.Project.ItemId).Number,
                    scope.Files, scope.State, review.Changed, previous?.Candidates.SingleOrDefault(c => c.Project.Id == scope.Project.Id && !c.WasAccepted),
                    calculation, calculation ? localDrafts.GetValueOrDefault(scope.Project.Id) : null));
            review.Selected = review.Candidates.FirstOrDefault(c => c.Draft is not null) ?? review.Candidates.FirstOrDefault();
            review.Changed();
            previous = null; localDrafts.Clear();
            _reportReview = (order.Id, review);
            if (review.Candidates.Any(c => CalculationProjectIds(order).Contains(c.Project.Id) && c.NeedsAttention)) MarkResultStale();
            NotifyCompleteness();
        });
        return Error is null ? review : null;
    }

    private void RequireUnchangedReportDrafts(WorkflowOrder order)
    {
        if (Drafts.Any(d => d.IsDirty && d.Saved != order.Specifications.SingleOrDefault(s => s.Id == d.Id)?.Current))
            throw new WorkflowValidationException("Заявка изменена другим пользователем. Ручные правки сохранены; проверьте изменения (F5).");
    }
    private void LoadReportOrder(WorkflowOrder order, IReadOnlySet<Guid>? replaced = null)
    {
        var drafts = Drafts.Where(d => d.IsDirty && replaced?.Contains(d.Id) != true).ToArray();
        var selected = SelectedProject?.Project.Id;
        LoadOrder(order);
        foreach (var draft in drafts)
        {
            var index = Drafts.ToList().FindIndex(d => d.Id == draft.Id);
            if (index >= 0) Drafts[index] = draft;
        }
        RefreshProjects(selected); NotifyOrder();
    }

    private async Task<string?> ApplyReportReviewAsync(WorkflowOrder previewOrder, Guid actor,
        IReadOnlyList<WorkflowReportCandidate> candidates, IWorkflowWorkspaceStore source, CancellationToken token, bool calculation = false)
    {
        var saved = false; var applied = false;
        await RunAsync(async () =>
        {
            if (!ReferenceEquals(source, _store) || CurrentOrder?.Id != previewOrder.Id || _cuttingTaskDetached) throw new WorkflowConflictException();
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
            if (!calculation) RequireUnchangedReportDrafts(previewOrder);
            foreach (var candidate in candidates)
            {
                var prior = previewOrder.Specifications.SingleOrDefault(s => s.Id == candidate.Project.Id)?.Current;
                await Task.Run(() => WorkflowCalculationSnapshots.PreserveAsync(previewOrder, candidate.Project.Id, actor, prior, null,
                    candidate.Snapshot!.SourcePath, token), token);
                if (candidate.HasUnpreservedEdits && candidate.Draft is { } edited)
                    await Task.Run(() => WorkflowCalculationSnapshots.PreserveAsync(previewOrder, candidate.Project.Id, actor, edited.Saved, edited,
                        candidate.Snapshot!.SourcePath, token), token);
            }
            WorkflowWorkspace savedWorkspace;
            try { savedWorkspace = await source.WriteOrderAsync(previewOrder.Id, previewOrder.Version, book =>
            {
                var next = book;
                foreach (var entry in accepted)
                    next = WorkflowChanges.SaveSpecification(next, previewOrder.Id, next.Orders.Single(o => o.Id == previewOrder.Id).Version,
                        entry.Draft.Id, entry.Parameters, entry.Number, entry.Date, actor, DateTimeOffset.UtcNow,
                        entry.Draft.SourcePath, entry.Draft.ImportProfile, entry.Rate, entry.Report);
                return next;
            }, token); }
            catch (WorkflowConflictException) { if (calculation) CanRetryCalculation = true; throw; }
            saved = true;
            if (!ReferenceEquals(source, _store) || CurrentOrder?.Id != previewOrder.Id || _cuttingTaskDetached)
                throw new WorkflowValidationException("Расчёты сохранены. Откройте прежнюю заявку и обновите журнал (F5), чтобы увидеть результат.");
            _workspace = savedWorkspace; RefreshOrders(preserveRows: true); LoadReportOrder(_workspace.Orders.Single(o => o.Id == previewOrder.Id), candidates.Select(c => c.Project.Id).ToHashSet()); applied = true;
            var bending = accepted.Where(entry => entry.Parameters.HasBending && entry.Draft.Saved?.Parameters.HasBending != true).Select(entry => entry.Draft.Id).ToArray();
            if (bending.Length > 0) await EnsureSavedBendingFoldersAsync(CurrentOrder!, bending, token);
        });
        if (!saved && Error is null) token.ThrowIfCancellationRequested();
        return saved && applied ? null : Error ?? "Отчёты не сохранены; подбор остался в окне.";
    }
}
