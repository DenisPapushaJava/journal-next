using System.Net;
using System.Text.Json;
using Jornal.Desktop.Updates;
using Velopack;
using Velopack.Sources;

namespace Jornal.Workflow.Desktop;

public sealed class WorkflowAppUpdater : IAppUpdater
{
    private readonly VelopackAppUpdater? _inner;
    public WorkflowAppUpdater(Uri? repository = null, Func<bool, UpdateManager>? managerFactory = null)
    {
        if (repository is not null) WorkflowIdentity.ValidateRepository(repository);
        if (repository is null && managerFactory is null) return;
        _inner = new(preview =>
        {
            var manager = managerFactory?.Invoke(preview) ?? new UpdateManager(new WorkflowGithubSource(repository!, preview),
                new UpdateOptions { ExplicitChannel = WorkflowIdentity.Channel, AllowVersionDowngrade = false });
            // Проверка установки ДО запроса feed: чужая установка не может искать обновления нового продукта.
            if (manager.IsInstalled && !string.Equals(manager.AppId, WorkflowIdentity.PackageId, StringComparison.Ordinal))
                throw new InvalidDataException("Эта установка принадлежит другому варианту Journal.");
            return manager;
        });
    }
    public string CurrentVersion => _inner?.CurrentVersion ?? WorkflowIdentity.Version;
    public bool IsInstalled => _inner?.IsInstalled ?? false;
    public Task<AppUpdate?> CheckAsync(bool preview, CancellationToken cancellationToken) =>
        (_inner ?? throw new InvalidOperationException("Отдельный канал обновлений ещё не подключён.")).CheckAsync(preview, cancellationToken);
    public Task DownloadAsync(AppUpdate update, IProgress<int> progress, CancellationToken cancellationToken) =>
        (_inner ?? throw new InvalidOperationException("Отдельный канал обновлений ещё не подключён.")).DownloadAsync(update, progress, cancellationToken);
    public void ScheduleRestart(AppUpdate update) =>
        (_inner ?? throw new InvalidOperationException("Отдельный канал обновлений ещё не подключён.")).ScheduleRestart(update);
}

internal sealed class WorkflowGithubSource(Uri repository, bool preview) : GithubSource(repository.AbsoluteUri.TrimEnd('/'), null, preview)
{
    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        if (includePrereleases) return await base.GetReleases(true);
        try
        {
            var json = await Downloader.DownloadString("https://api.github.com/repos/" + repository.AbsolutePath.Trim('/') + "/releases/latest",
                GetRequestHeaders("application/vnd.github+json"), timeout: 0.5);
            var release = JsonSerializer.Deserialize<GithubRelease>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            return release is { Prerelease: false } ? [release] : [];
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return []; }
    }
}
