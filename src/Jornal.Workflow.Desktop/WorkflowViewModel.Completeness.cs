using CommunityToolkit.Mvvm.Input;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private enum ProjectWork { Ready, Waiting, Review }
    private sealed record ProjectProgress(Guid ProjectId, ProjectWork Work);

    // One entry per included position. Other variants and trashed projects do not
    // inflate the denominator; file changes are known only after an explicit scan.
    private ProjectProgress[] ProjectProgressItems()
    {
        if (CurrentOrder is not { } order) return [];
        var projects = order.ActiveProjects.ToLookup(p => p.ItemId);
        var drafts = Drafts.ToDictionary(d => d.Id);
        var reports = CurrentReportReview?.Candidates.ToDictionary(c => c.Project.Id);
        var result = new List<ProjectProgress>();
        foreach (var item in order.Items.Where(i => i.IsIncluded).OrderBy(i => i.Number))
        {
            var variants = projects[item.Id].OrderByDescending(p => p.Variant).ToArray();
            if (variants.Length == 0) continue;
            var current = variants.SingleOrDefault(p => p.Id == item.CurrentProjectId);
            var project = current ?? variants[0];
            var saved = order.Specifications.SingleOrDefault(s => s.Id == project.Id);
            var changed = variants.FirstOrDefault(p => p.Rename is not null ||
                drafts.GetValueOrDefault(p.Id)?.IsDirty == true ||
                reports?.GetValueOrDefault(p.Id)?.NeedsAttention == true);
            if (changed is not null) result.Add(new(changed.Id, ProjectWork.Review));
            else if (current is null || variants[0].Variant > item.ReviewedThroughVariant)
                result.Add(new(variants[0].Id, ProjectWork.Review));
            else result.Add(new(project.Id, saved is null ? ProjectWork.Waiting : ProjectWork.Ready));
        }
        foreach (var saved in order.Specifications.Where(s => !order.Projects.Any(p => p.Id == s.Id)))
            result.Add(new(saved.Id, drafts.GetValueOrDefault(saved.Id)?.IsDirty == true ? ProjectWork.Review : ProjectWork.Ready));
        return result.ToArray();
    }

    public string CompletenessCaption
    {
        get { var items = ProjectProgressItems(); return $"Готово {items.Count(p => p.Work == ProjectWork.Ready)} из {items.Length}"; }
    }
    public int WaitingProjectCount => ProjectProgressItems().Count(p => p.Work == ProjectWork.Waiting);
    public int ReviewProjectCount => ProjectProgressItems().Count(p => p.Work == ProjectWork.Review);
    public string WaitingProjectsCaption => $"Ожидают спецификацию: {WaitingProjectCount}";
    public string ReviewProjectsCaption => $"Требуют проверки: {ReviewProjectCount}";
    public bool HasWaitingProjects => WaitingProjectCount > 0;
    public bool HasReviewProjects => ReviewProjectCount > 0;
    private void NotifyCompleteness()
    {
        NotifyCuttingTask();
        foreach (var name in new[] { nameof(CompletenessCaption), nameof(WaitingProjectCount), nameof(ReviewProjectCount),
            nameof(WaitingProjectsCaption), nameof(ReviewProjectsCaption), nameof(HasWaitingProjects), nameof(HasReviewProjects),
            nameof(ReportImportNotice), nameof(HasReportImportNotice), nameof(AcceptedReportsCaption), nameof(HasPendingEdits) }) OnPropertyChanged(name);
    }
    [RelayCommand] private void GoToWaitingProject() => GoToProject(ProjectWork.Waiting);
    [RelayCommand] private void GoToReviewProject() => GoToProject(ProjectWork.Review);
    private void GoToProject(ProjectWork work)
    {
        if (!CanUse || !IsCurrentOrderActive || IsPlanning) return;
        var matches = ProjectProgressItems().Where(p => p.Work == work).Select(p => p.ProjectId).ToArray();
        if (matches.Length == 0) return;
        var index = Array.IndexOf(matches, SelectedProject?.Project.Id ?? Guid.Empty);
        OrderTabIndex = 0; ShowTrashedProjects = false;
        SelectedProject = Projects.Single(p => p.Project.Id == matches[(index + 1) % matches.Length]);
    }
}
