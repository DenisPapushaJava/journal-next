using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Jornal.Parsing.Documents;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public static class WorkflowCalculationExchange
{
    public const string Extension = ".calculation.zip";
    public const int MaximumManifest = 1024 * 1024;
    private const int MaximumReport = 32 * 1024 * 1024;
    private const int MaximumReports = 128 * 1024 * 1024;
    private const string ManifestName = "calculation.json";

    public sealed record Manifest(int Format, Guid DatabaseId, WorkflowOfficeCalculationDraft Draft);
    private sealed record Input(WorkflowOfficeCalculationDraftEntry Entry, ReportFileSnapshot Snapshot,
        ReportValues Baseline, SpecificationState? Previous);
    public sealed record Result(WorkflowWorkspace Workspace, WorkflowOrder Order, bool AlreadySaved, string? PublicationProblem, bool PublicationDeferred);

    public static async Task<Result> ImportAsync(WorkflowMdbStore store, Guid? actor, string path, CancellationToken token, string? requiredProjectRoot = null)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        if (file.Length > MaximumReports + MaximumManifest * 2L) throw InvalidPackage();
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count is < 2 or > 101) throw InvalidPackage();
        var metadata = archive.GetEntry(ManifestName);
        if (metadata is null || metadata.Length is <= 0 or > MaximumManifest) throw InvalidPackage();
        var metadataBytes = new byte[(int)metadata.Length];
        await using (var input = metadata.Open())
        {
            await input.ReadExactlyAsync(metadataBytes, token);
            if (input.ReadByte() != -1) throw InvalidPackage();
        }
        var manifest = JsonSerializer.Deserialize<Manifest>(metadataBytes) ?? throw InvalidPackage();
        Validate(manifest);
        var draft = manifest.Draft;
        var author = actor ?? draft.Actor;
        var expectedEntries = draft.Entries.Select(EntryName).Append(ManifestName).ToHashSet(StringComparer.Ordinal);
        if (archive.Entries.Count != expectedEntries.Count || !expectedEntries.SetEquals(archive.Entries.Select(entry => entry.FullName)))
            throw InvalidPackage();

        WorkflowWorkspace book;
        try
        {
            if (store.RequiresRefresh) await store.ReadAsync(token);
            book = await store.ReadOrderAsync(draft.OrderId, token);
        }
        catch (WorkflowValidationException ex) when (requiredProjectRoot is not null)
        { throw new IOException("Сервер пока не смог прочитать офисную MDB.", ex); }
        if (store.DatabaseId != manifest.DatabaseId) throw new WorkflowValidationException("Этот пакет предназначен для другой базы заявок.");
        if (requiredProjectRoot is not null && !string.Equals(
                book.ProjectRoot is null ? null : Path.TrimEndingDirectorySeparator(book.ProjectRoot),
                Path.TrimEndingDirectorySeparator(requiredProjectRoot), OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new WorkflowValidationException("Рабочая папка проектов в MDB отличается от настроек сервера.");
        var order = book.Orders.SingleOrDefault(candidate => candidate.Id == draft.OrderId && !candidate.IsDeleted)
            ?? throw new WorkflowValidationException("Заявка из пакета недоступна в офисе.");
        if (draft.IsSavedIn(order)) return await FinishPublicationAsync(store, book, order, draft, alreadySaved: true, token);
        if (order.Version != draft.OrderVersion || order.HasProjectRename)
            throw new WorkflowValidationException($"Заявка {order.DisplayNumber} изменилась в офисе. Домашний пакет сохранён для сверки.");

        var inputs = new List<Input>();
        var calculation = book;
        foreach (var entry in draft.Entries)
        {
            var project = order.ActiveProjects.SingleOrDefault(candidate => candidate.Id == entry.ProjectId)
                ?? throw new WorkflowConflictException();
            var existing = order.Specifications.SingleOrDefault(candidate => candidate.Id == entry.ProjectId);
            if (!book.Rates.Contains(entry.Rate))
                throw new WorkflowValidationException($"Тариф проекта «{project.Name}» изменился. Пересчитайте заявку по офисным ценам.");
            calculation = Save(calculation, order.Id, entry, author, existing?.Current.SourcePath, existing?.Current.Report);
            var report = entry.Captured!;
            var folder = (project.NativePath is { } native ? WorkflowOfficeProjectPaths.InRoot(book.ProjectRoot, native) : null)
                ?? throw new WorkflowValidationException($"Рабочая папка проекта «{project.Name}» недоступна.");
            if (!Directory.Exists(folder)) throw new WorkflowValidationException($"Рабочая папка проекта «{project.Name}» недоступна.");
            var destination = WorkflowOfficeProjectPaths.FileInProject(folder, report.FileName) ?? throw InvalidPackage();
            var packed = archive.GetEntry(EntryName(entry))!;
            if (packed.Length != report.Length) throw InvalidPackage();
            var bytes = new byte[(int)report.Length];
            await using (var stream = packed.Open())
            {
                await stream.ReadExactlyAsync(bytes, token);
                if (stream.ReadByte() != -1) throw InvalidPackage();
            }
            ReportFileSnapshot snapshot;
            try { snapshot = await WorkflowReportFiles.ReadCapturedAsync(destination, bytes, report.Sha256, token); }
            catch (DocumentRejectedException ex)
            { throw new WorkflowValidationException($"Не удалось распознать спецификацию «{report.FileName}». {ex.Message}"); }
            if (!WorkflowReportFiles.Matches(snapshot, project, existing, book.Machines.SingleOrDefault(machine => machine.Id == project.MachineId)))
                throw new WorkflowValidationException($"Спецификация «{report.FileName}» не совпадает с проектом «{project.Name}».");
            inputs.Add(new(entry, snapshot, report.Parsed, existing?.Current));
        }

        var accepted = new Dictionary<Guid, AcceptedReport>();
        var leases = new List<FileStream>();
        WorkflowWorkspace saved;
        try
        {
            foreach (var report in inputs)
            {
                if (report.Previous is { } previous)
                    await WorkflowCalculationHistory.SaveAsync(Path.GetDirectoryName(report.Snapshot.SourcePath)!,
                        new(1, order.Id, report.Entry.ProjectId, DateTimeOffset.UtcNow, author, previous, null) { OrderVersion = order.Version }, token);
                if (File.Exists(report.Snapshot.SourcePath))
                    leases.Add(await OpenVerifiedOfficeFileAsync(report.Snapshot.SourcePath, report.Snapshot.Sha256,
                        PreviousHash(report.Previous, report.Snapshot.SourcePath), token));
                accepted.Add(report.Entry.ProjectId, await WorkflowReportFiles.ArchiveAsync(report.Snapshot, report.Baseline, token));
            }
            saved = await store.WriteOrderAsync(order.Id, order.Version, latest =>
            {
                if (book.ProjectRoot != latest.ProjectRoot || draft.Entries.Any(entry => !latest.Rates.Contains(entry.Rate)))
                    throw new WorkflowValidationException("Общая папка проектов или тариф изменились. Обновите журнал и проверьте расчёт.");
                foreach (var report in inputs)
                    latest = Save(latest, order.Id, report.Entry, author, report.Snapshot.SourcePath, accepted[report.Entry.ProjectId]);
                return latest;
            }, token);
        }
        finally { foreach (var lease in leases) await lease.DisposeAsync(); }
        var current = saved.Orders.SingleOrDefault(candidate => candidate.Id == order.Id);
        if (current is null || !draft.IsSavedIn(current)) throw new WorkflowOutcomeUnknownException();
        return await FinishPublicationAsync(store, saved, current, draft, alreadySaved: false, token);
    }

    private static async Task<Result> FinishPublicationAsync(WorkflowMdbStore store, WorkflowWorkspace book, WorkflowOrder order,
        WorkflowOfficeCalculationDraft draft, bool alreadySaved, CancellationToken token)
    {
        string? problem = null;
        var deferred = false;
        foreach (var entry in draft.Entries)
        {
            var project = order.ActiveProjects.Single(candidate => candidate.Id == entry.ProjectId);
            var state = order.Specifications.Single(candidate => candidate.Id == entry.ProjectId).Current;
            if (state.Report is not { } archived) continue;
            var folder = project.NativePath is { } native ? WorkflowOfficeProjectPaths.InRoot(book.ProjectRoot, native) : null;
            var path = folder is null ? null : WorkflowOfficeProjectPaths.FileInProject(folder, archived.FileName);
            if (path is null)
            {
                problem ??= $"Рабочая папка проекта «{project.Name}» недоступна.";
                continue;
            }
            if (!string.Equals(state.SourcePath, path, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            try
            {
                var existing = File.Exists(path) ? (await WorkflowOfficeProjectPaths.FingerprintAsync(path, token)).Hash : null;
                if (existing != archived.Sha256)
                {
                    if (existing is not null)
                    {
                        var history = await WorkflowCalculationHistory.ReadAsync(folder!, order.Id, project.Id, token);
                        if (!history.Snapshots.Any(item => item.OrderVersion == draft.OrderVersion && PreviousHash(item.State, path) == existing))
                            throw OfficeFileChanged(path);
                    }
                    WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(archived.ArchivePath);
                    var bytes = await WorkflowReportFiles.ReadVerifiedArchiveAsync(archived, token);
                    await PublishFileAsync(store, new(order.Id, order.Version, book.ProjectRoot!, path, Guid.NewGuid(), archived.Sha256, existing), bytes, token);
                }
                if (entry.Parameters.HasBending) await WorkflowBendingFiles.EnsureAsync(project, token);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException)
            {
                deferred |= ex is not WorkflowValidationException;
                problem ??= ex is WorkflowValidationException ? ex.Message : $"Файлы проекта «{project.Name}» недоступны. Проверьте рабочую папку.";
            }
        }
        return new(book, order, alreadySaved, problem, deferred);
    }

    private static WorkflowWorkspace Save(WorkflowWorkspace book, Guid order, WorkflowOfficeCalculationDraftEntry entry,
        Guid actor, string? source, AcceptedReport? report) => WorkflowChanges.SaveSpecification(book, order,
            book.Orders.Single(candidate => candidate.Id == order).Version, entry.ProjectId, entry.Parameters, entry.Number.Trim(),
            entry.Date, actor, DateTimeOffset.UtcNow, source, entry.Profile, entry.Rate, report);

    private static string? PreviousHash(SpecificationState? state, string path) =>
        string.Equals(state?.SourcePath, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? state?.Report?.Sha256 : null;

    private static WorkflowValidationException OfficeFileChanged(string path) =>
        new($"Спецификация «{Path.GetFileName(path)}» в офисе отличается. Сверьте её с домашним пакетом.");

    private static async Task<FileStream> OpenVerifiedOfficeFileAsync(string path, string hash, string? previousHash, CancellationToken token)
    {
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        try
        {
            if (stream.Length is <= 0 or > MaximumReport) throw OfficeFileChanged(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            if (actual != hash && actual != previousHash) throw OfficeFileChanged(path);
            return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }

    private static string StagedFile(WorkflowCalculationFilePublication file) =>
        Path.Combine(Path.GetDirectoryName(file.Path)!, ".workflow-reports", $"{file.StagedId:N}.tmp");

    private static async Task PublishFileAsync(WorkflowMdbStore store, WorkflowCalculationFilePublication file, byte[] bytes, CancellationToken token)
    {
        var temporary = StagedFile(file);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(temporary);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                await output.WriteAsync(bytes, token);
                output.Flush(true);
            }
            await store.PublishCalculationFileAsync(file, token);
        }
        finally { File.Delete(temporary); }
    }

    internal static void PublishStaged(WorkflowCalculationFilePublication file, Action<Action> withCurrentOrder)
    {
        if (file.StagedId == Guid.Empty || !Path.IsPathFullyQualified(file.ProjectRoot) ||
            WorkflowOfficeProjectPaths.InRoot(file.ProjectRoot, file.Path) is not { } folder ||
            WorkflowOfficeProjectPaths.FileInProject(folder, Path.GetFileName(file.Path)) != file.Path)
            throw InvalidPackage();
        var temporary = StagedFile(file);
        WorkflowOfficeProjectPaths.RequireOrdinaryCopyPath(temporary);
        using (var staged = File.OpenRead(temporary))
            if (!MatchesPublicationHash(staged, file.Sha256))
                throw new IOException("Временная спецификация изменилась. Повторите подготовку из сохранённой копии.");

        var backup = file.PreviousHash is null ? null : temporary + ".previous";
        using var lease = backup is null ? null : new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (lease is not null && !MatchesPublicationHash(lease, file.PreviousHash!))
            throw OfficeFileChanged(file.Path);
        withCurrentOrder(() =>
        {
            if (backup is not null) File.Replace(temporary, file.Path, backup);
            else
            {
                try { File.Move(temporary, file.Path, overwrite: false); }
                catch (IOException) when (File.Exists(file.Path)) { }
            }
        });
        using var published = File.OpenRead(file.Path);
        if (!MatchesPublicationHash(published, file.Sha256))
            throw backup is null ? OfficeFileChanged(file.Path) :
                new WorkflowValidationException($"Не удалось подтвердить новый файл «{Path.GetFileName(file.Path)}». Резервная копия рабочего файла: {backup}");
        if (backup is null) return;
        using (var previous = File.OpenRead(backup))
            if (!MatchesPublicationHash(previous, file.PreviousHash!))
                throw new WorkflowValidationException($"Офисный файл изменился во время записи. Обе версии сохранены. Резервная копия: {backup}");
        File.Delete(backup);
    }

    private static bool MatchesPublicationHash(FileStream stream, string hash) =>
        stream.Length is > 0 and <= MaximumReport && Convert.ToHexString(SHA256.HashData(stream)) == hash;

    public static string EntryName(WorkflowOfficeCalculationDraftEntry entry) => $"{entry.ProjectId:N}/{entry.Captured!.FileName}";

    public static void Validate(Manifest manifest)
    {
        var draft = manifest.Draft;
        if (manifest.Format != 1 || manifest.DatabaseId == Guid.Empty || draft is null || draft.Id == Guid.Empty ||
            draft.OrderId == Guid.Empty || draft.Actor == Guid.Empty || draft.OrderVersion <= 0 || draft.Entries is not { Length: > 0 and <= 100 } ||
            draft.Entries.Any(entry => entry is null || entry.ProjectId == Guid.Empty || entry.Date == default ||
                string.IsNullOrWhiteSpace(entry.Number) || entry.Parameters is null || entry.Rate is null || entry.Captured is null) ||
            draft.Entries.Select(entry => entry.ProjectId).Distinct().Count() != draft.Entries.Length)
            throw InvalidPackage();
        foreach (var entry in draft.Entries)
        {
            var captured = entry.Captured!;
            if (captured.FileName is not { Length: > 0 and <= 255 } || captured.FileName.IndexOfAny(['/', '\\', ':']) >= 0 ||
                captured.FileName.Any(char.IsControl) || !WorkflowReportFiles.IsReport(captured.FileName, entry.Profile) ||
                captured.Length is <= 0 or > MaximumReport || captured.Sha256 is not { Length: 64 } || !captured.Sha256.All(char.IsAsciiHexDigit) ||
                entry.ReportHash != captured.Sha256 || captured.Parsed is null || captured.ArchivePath != EntryName(entry)) throw InvalidPackage();
        }
        if (draft.Entries.Sum(entry => entry.Captured!.Length) > MaximumReports) throw InvalidPackage();
    }

    private static InvalidDataException InvalidPackage() => new("Пакет расчёта повреждён или ещё синхронизируется. Повторите проверку после загрузки.");
}
