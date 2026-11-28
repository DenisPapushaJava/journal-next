using System.IO.Compression;
using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

internal static class WorkflowCalculationExport
{
    internal static async Task<string?> ExportAsync(WorkflowOfficeStorageClient client, Guid order, string folder, CancellationToken token)
    {
        using var lease = client.LockCalculationDraft(order);
        var draft = client.ReadCalculationDraft(order) ?? throw new WorkflowValidationException("Нет сохранённого черновика расчёта.");
        return await ExportDraftAsync(client, draft, folder, token);
    }

    internal static async Task<string?> ExportDraftAsync(WorkflowOfficeStorageClient client, WorkflowOfficeCalculationDraft draft, string folder, CancellationToken token)
    {
        if (!Directory.Exists(folder)) throw new WorkflowValidationException("Папка обмена расчётами недоступна. Проверьте путь в настройках.");
        var prefix = draft.OrderNumber is { Length: > 0 and <= 36 } number && number.All(c => char.IsAsciiDigit(c) || c == '-') ? number + "-" : "";
        var name = $"{prefix}{draft.Id:N}{WorkflowCalculationExchange.Extension}";
        var target = Path.Combine(folder, name);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(target);
        if (File.Exists(Path.Combine(folder, "Принятые", name)))
            return "Сервер принял расчёт. Офисная версия появится в журнале после подключения.";
        if (File.Exists(Path.Combine(folder, "На проверку", name)))
            return "Сервер остановил пакет для сверки. Причина указана рядом с пакетом в папке «На проверку».";
        if (File.Exists(target)) return null;
        // Package paths describe archive entries. Only the receiver chooses working paths.
        if (draft.Entries.Any(entry => entry.Captured is null))
            throw new WorkflowValidationException("Для выгрузки нужны сохранённые снимки спецификаций всех проектов черновика.");
        var entries = draft.Entries.Select(entry => entry with { Captured = entry.Captured! with { ArchivePath = WorkflowCalculationExchange.EntryName(entry) } }).ToArray();
        var manifest = new WorkflowCalculationExchange.Manifest(1, client.Settings.DatabaseId, draft with { Entries = entries, QueuedForOffice = false });
        WorkflowCalculationExchange.Validate(manifest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (bytes.Length > WorkflowCalculationExchange.MaximumManifest) throw new WorkflowValidationException("Слишком большой черновик расчёта.");

        var temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await using (var metadata = archive.CreateEntry("calculation.json").Open()) await metadata.WriteAsync(bytes, token);
                    foreach (var entry in draft.Entries)
                    {
                        var report = entry.Captured!;
                        var reportPath = client.DraftReportPath(draft, entry, report);
                        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(reportPath);
                        var data = await WorkflowReportFiles.ReadVerifiedArchiveAsync(report with { ArchivePath = reportPath }, token);
                        if (data.LongLength != report.Length)
                            throw new WorkflowValidationException("Снимок спецификации изменился. Проверьте сохранённый черновик.");
                        await using var file = archive.CreateEntry(WorkflowCalculationExchange.EntryName(entry)).Open();
                        await file.WriteAsync(data, token);
                    }
                }
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            try { File.Move(temporary, target, overwrite: false); }
            catch (IOException) when (File.Exists(target)) { }
        }
        finally { File.Delete(temporary); }
        return null;
    }
}
