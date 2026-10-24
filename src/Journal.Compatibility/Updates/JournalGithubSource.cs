using System.Net;
using System.Text.Json;
using Velopack.Sources;

namespace Jornal.Desktop.Updates;

public sealed class JournalGithubSource(bool preview, IFileDownloader? downloader = null)
    : GithubSource(VelopackAppUpdater.RepositoryUrl, null, preview, downloader)
{
    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        if (includePrereleases) return await base.GetReleases(true);
        // Стандартный GithubSource просматривает только 10 выпусков: частые beta могут скрыть стабильный.
        try
        {
            var json = await Downloader.DownloadString("https://api.github.com/repos/DenisPapushaJava/journal-v2.0/releases/latest",
                GetRequestHeaders("application/vnd.github+json"), timeout: 0.5);
            var release = JsonSerializer.Deserialize<GithubRelease>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
            return release is { Prerelease: false } ? [release] : [];
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound) { return []; }
    }
}
