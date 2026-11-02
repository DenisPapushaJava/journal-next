using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal static class WorkflowCalculationSnapshots
{
    internal static SpecificationDraft MergeLocal(WorkflowSpecification? saved, string? native, SpecificationDraft local)
    {
        var result = saved is null ? new SpecificationDraft { Id = local.Id } : new(saved, native);
        var before = local.Saved is null ? new SpecificationDraft() : new(new(local.Id, local.Saved));
        var values = WorkflowReportCandidate.Parts(WorkflowReportCandidate.Values(local));
        var baseline = WorkflowReportCandidate.Parts(WorkflowReportCandidate.Values(before));
        for (var i = 0; i < values.Length; i++) if (values[i] != baseline[i]) WorkflowReportCandidate.Set(result, i, values[i]);
        if (local.Coefficient != before.Coefficient) result.Coefficient = local.Coefficient;
        if (local.HasBending != before.HasBending) result.HasBending = local.HasBending;
        if (local.HasChamfer != before.HasChamfer) result.HasChamfer = local.HasChamfer;
        if (local.Comment != before.Comment) result.Comment = local.Comment;
        result.IncomingReport = local.IncomingReport; result.SourcePath = local.SourcePath; result.ImportProfile = local.ImportProfile;
        result.MaterialNeedsReview = local.MaterialNeedsReview; result.MaterialConfirmed = local.MaterialConfirmed;
        return result;
    }

    internal static string? ProjectFolder(WorkflowOrder order, Guid projectId, string? sourcePath = null) =>
        Path.GetDirectoryName(order.Projects.SingleOrDefault(p => p.Id == projectId)?.NativePath ?? sourcePath ??
            order.Specifications.SingleOrDefault(s => s.Id == projectId)?.Current.SourcePath);

    internal static async Task PreserveAsync(WorkflowOrder order, Guid projectId, Guid actor, SpecificationState? state,
        SpecificationDraft? draft, string? sourcePath, CancellationToken token)
    {
        await PreserveAsync(ProjectFolder(order, projectId, sourcePath), order.Id, projectId, actor, state, draft, token, order.Version);
    }

    internal static async Task PreserveAsync(string? folder, Guid orderId, Guid projectId, Guid actor,
        SpecificationState? state, SpecificationDraft? draft, CancellationToken token, long orderVersion = 0)
    {
        if (state is null && draft is null) return;
        if (folder is null) throw new WorkflowValidationException("Для сохранения предыдущего расчёта укажите рабочую папку проекта.");
        CalculationDraftSnapshot? raw = null;
        if (draft is not null)
        {
            var report = draft.Saved?.Report;
            if (draft.IncomingReport is { } incoming)
            {
                // The working file may already be replaced. Preserve the captured bytes,
                // not whatever now happens to be at its path.
                report = await WorkflowReportFiles.ArchiveAsync(incoming, WorkflowReportCandidate.Values(draft), token);
                report = report with { Id = new Guid(Convert.FromHexString(report.Sha256[..32])) };
            }
            raw = new(WorkflowReportCandidate.Values(draft), draft.Coefficient, draft.HasBending, draft.HasChamfer,
                draft.Comment, draft.PricePerMeter, draft.PricePerPierce, draft.Quote, report);
        }
        try { await WorkflowCalculationHistory.SaveAsync(folder, new(1, orderId, projectId, DateTimeOffset.UtcNow, actor, state, raw) { OrderVersion = orderVersion }, token); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new WorkflowValidationException("Не удалось сохранить предыдущий расчёт в папке проекта. Проверьте доступ и повторите действие. Текущий расчёт не заменён."); }
    }
}
