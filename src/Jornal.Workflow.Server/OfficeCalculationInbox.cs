using System.Text.Json;
using Jornal.Infrastructure.Access;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Server;

internal sealed class OfficeCalculationInbox(string folder, string database, string projects, IWorkflowAccessTransport transport,
    ILogger<OfficeCalculationInbox> logger, OfficeDiagnostics diagnostics) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var clock = new PeriodicTimer(TimeSpan.FromSeconds(20));
        do
        {
            try { await ProcessAvailableAsync(stoppingToken); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
            { logger.LogWarning(ex, "Папка обмена расчётами недоступна."); }
        } while (await clock.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ProcessAvailableAsync(CancellationToken token)
    {
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(folder);
        if (!Directory.Exists(folder) || !File.Exists(database) || !Directory.Exists(projects)) return;
        foreach (var path in Directory.EnumerateFiles(folder, "*" + WorkflowCalculationExchange.Extension).OrderBy(File.GetLastWriteTimeUtc))
        {
            token.ThrowIfCancellationRequested();
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromSeconds(8)) continue;
            try { await ProcessAsync(path, token); }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogWarning(ex, "Не удалось обработать пакет расчёта.");
                diagnostics.Record("calculation-exchange-unavailable", "calculation-inbox", ex);
            }
        }
    }

    private async Task ProcessAsync(string path, CancellationToken token)
    {
        string? problem;
        try
        {
            WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
            var store = new WorkflowMdbStore(database, transport);
            var result = await WorkflowCalculationExchange.ImportAsync(store, null, path, token, projects);
            if (result.PublicationDeferred) return;
            problem = result.PublicationProblem;
        }
        catch (Exception ex) when (ex is WorkflowConflictException or WorkflowOutcomeUnknownException) { return; }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or EndOfStreamException)
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(2)) return;
            problem = "Пакет повреждён или не полностью синхронизирован. Проверьте Яндекс Диск.";
        }
        catch (WorkflowValidationException ex) { problem = ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AccessHostBlockedException)
        {
            diagnostics.Record("calculation-exchange-unavailable", "calculation-inbox", ex);
            return;
        }

        var destination = Path.Combine(folder, problem is null ? "Принятые" : "На проверку", Path.GetFileName(path));
        if (File.Exists(destination))
            destination = Path.Combine(Path.GetDirectoryName(destination)!, Guid.NewGuid().ToString("N") + WorkflowCalculationExchange.Extension);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (problem is not null)
        {
            WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(destination + ".txt");
            await File.WriteAllTextAsync(destination + ".txt", problem, token);
        }
        File.Move(path, destination, overwrite: false);
    }
}
