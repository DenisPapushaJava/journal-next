using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    internal bool SupportsOfficeCopyCleanup => OperatingSystem.IsWindows() && IsRemoteStorage;
    internal WorkflowOfficeStorageClient? OfficeCleanupClient =>
        SupportsOfficeCopyCleanup && CanUse && !_cuttingTaskDetached ? _connection.RemoteClient : null;

    internal string OfficeCleanupUnavailableReason => _connection.RemoteClient is null
        ? "Откройте «Настроить подключение…» и подключитесь к серверу заново."
        : "Дождитесь завершения текущей операции и попробуйте ещё раз.";

    internal Task<WorkflowOfficeCleanupPlan> PreviewOfficeCleanupAsync(WorkflowOfficeStorageClient client, CancellationToken token)
    {
        var (protectedProjects, projectRoot, shopRoot) = OfficeCleanupContext(client);
        var orders = _workspace.Orders;
        return Task.Run(() => client.PreviewCleanupAsync(orders, protectedProjects, projectRoot, shopRoot, token), token);
    }

    internal Task<WorkflowOfficeCleanupResult> RemoveOfficeCopiesAsync(WorkflowOfficeStorageClient client,
        WorkflowOfficeCleanupPlan plan, CancellationToken token)
    {
        var (protectedProjects, projectRoot, shopRoot) = OfficeCleanupContext(client);
        return Task.Run(() => client.RemoveCleanCopiesAsync(plan, protectedProjects, projectRoot, shopRoot, token), token);
    }

    private (HashSet<Guid> Protected, string? ProjectRoot, string? ShopRoot) OfficeCleanupContext(WorkflowOfficeStorageClient client)
    {
        if (!ReferenceEquals(client, OfficeCleanupClient))
            throw new WorkflowValidationException("Дождитесь завершения текущей операции и начните проверку заново.");
        var protectedProjects = Drafts.Concat(_cards.Values.SelectMany(card => card.Drafts))
            .Where(draft => draft.IsDirty).Select(draft => draft.Id).ToHashSet();
        if (_reportReview is { } session)
            foreach (var candidate in session.Review.Candidates.Where(candidate => !candidate.WasAccepted && candidate.Draft?.IsDirty == true))
                protectedProjects.Add(candidate.Project.Id);
        return (protectedProjects, _workspace.ProjectRoot, ShopRoot);
    }
}
