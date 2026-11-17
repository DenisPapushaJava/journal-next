using System.Security.Cryptography;
using System.Text.Json;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public static class WorkflowManagerMapBundle
{
    internal const string MarkerFolder = ".workflow-manager-maps";
    internal const string FileSuffix = "-Блок-Карта";
    internal const string PreviousFileSuffix = " — карты раскроя";
    private sealed record BundleVersion(string InputsHash, string OutputHash);
    private sealed record Receipt(BundleVersion Current, BundleVersion? Previous);

    public static bool Supports(CuttingProject project, WorkflowMachine machine) =>
        !project.IsDeleted && project.Rename is null && project.ImportProfile == "hans-pdf/v1" &&
        machine.ImportProfile == project.ImportProfile && WorkflowShopDiscovery.MachineRule(machine) == WorkflowShopDiscovery.Rule.Hans &&
        project.NativePath is { } path && Path.IsPathFullyQualified(path) && Path.GetExtension(path).Equals(".dsp", StringComparison.OrdinalIgnoreCase);

    public static bool HasMultipleCards(CuttingProject project) => CountAvailableCards(project) == 2;
    public static int CountAvailableCards(CuttingProject project) => CountAvailableCards(project, limit: 2);

    private static int CountAvailableCards(CuttingProject project, int limit)
    {
        if (project.NativePath is not { } path || !Path.IsPathFullyQualified(path) || Path.GetDirectoryName(path) is not { } folder)
            return 0;
        try
        {
            ShopFileSystem.RequireOrdinaryPath(folder);
            return Directory.Exists(folder)
                ? WorkflowHansShopMaps.WithoutPairedGnf(ShopFileSystem.CandidateFiles(folder, [".doc"]).ToArray())
                    .Where(p => new FileInfo(p).Length > 0).Take(limit).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException) { return 0; }
    }

    public static async Task<string?> CreateAsync(CuttingProject project, WorkflowMachine machine, ShopMapBundle format, IWorkflowWordMapEditor? word,
        Func<CancellationToken, Task> validate, CancellationToken token)
    {
        if (!Supports(project, machine) || format is not (ShopMapBundle.Doc or ShopMapBundle.Pdf)) return null;
        var folder = Path.GetDirectoryName(project.NativePath!)!;
        var minimum = format == ShopMapBundle.Pdf ? 1 : 2;
        if (CountAvailableCards(project, minimum) < minimum) return null;
        var plan = await WorkflowShopDiscovery.DiscoverProjectAsync(project, machine, "", false, token, word, mapsOnly: true);
        if (plan.Problem is { } problem) throw new WorkflowValidationException(problem);
        var candidates = plan.Files.Where(f => !f.Ignored && (f.Length > 0 || !f.IsReadable)).ToArray();
        if (candidates.FirstOrDefault(f => !f.IsReadable || (!f.Suggested && !f.ConfirmedProjectLink)) is { } uncertain)
            throw new WorkflowValidationException($"Карта «{uncertain.Name}»: {uncertain.Reason}");
        if (candidates.Length < minimum) return null;
        var name = project.Name + FileSuffix + (format == ShopMapBundle.Doc ? ".doc" : ".pdf");
        return await PublishAsync(project, name, format, candidates, plan,
            plan.Files.Select(f => f.Name).Order(StringComparer.Ordinal).ToArray(), word, validate, token);
    }

    public static async Task<string?> CreateGnfAsync(CuttingProject project, WorkflowMachine machine, IWorkflowWordMapEditor? word,
        Func<CancellationToken, Task> validate, CancellationToken token)
    {
        if (!Supports(project, machine)) return null;
        var folder = Path.GetDirectoryName(project.NativePath!)!;
        ShopFileSystem.RequireOrdinaryPath(folder);
        if (!Directory.Exists(folder)) return null;
        var paths = ShopFileSystem.CandidateFiles(folder, [".doc"]).Take(2001).ToArray();
        if (paths.Length > 2000) return null; // The ordinary picker reports the folder limit.
        var pairs = WorkflowHansShopMaps.FindGnfPairs(paths);
        if (pairs.Count < 2) return null;
        var plan = await WorkflowShopDiscovery.DiscoverProjectAsync(project, machine, "", false, token, word, mapsOnly: true);
        if (plan.Problem is { } problem) throw new WorkflowValidationException(problem);
        var ordinaryByPath = plan.Files.Where(f => !f.Ignored).ToDictionary(f => f.SourcePath, StringComparer.Ordinal);
        var cards = new List<ShopFile>();
        var pairedCards = new Dictionary<string, ShopFile>(StringComparer.Ordinal);
        foreach (var (gnf, ordinaryPath) in pairs)
        {
            token.ThrowIfCancellationRequested();
            if (!ordinaryByPath.TryGetValue(ordinaryPath, out var ordinary)) continue;
            if (!ordinary.IsReadable || (!ordinary.Suggested && !ordinary.ConfirmedProjectLink))
                throw new WorkflowValidationException($"Карта «{ordinary.Name}»: {ordinary.Reason}");
            var (length, hash) = await ShopFileSystem.FingerprintAsync(gnf, token);
            if (length == 0) continue;
            var name = Path.GetFileName(gnf);
            pairedCards.Add(name, ordinary);
            cards.Add(new(name, gnf, length, hash, ShopFileKind.Map, true, ""));
        }
        if (cards.Count < 2) return null;
        return await PublishAsync(project, project.Name + FileSuffix + ".Gnf.doc", ShopMapBundle.Doc, cards.ToArray(), plan,
            paths.Select(Path.GetFileName).Cast<string>().Order(StringComparer.Ordinal).ToArray(), word, validate, token, pairedCards);
    }

    private static async Task<string> PublishAsync(CuttingProject project, string name, ShopMapBundle format,
        ShopFile[] candidates, ShopProjectFiles plan, string[] expectedNames, IWorkflowWordMapEditor? word,
        Func<CancellationToken, Task> validate, CancellationToken token, IReadOnlyDictionary<string, ShopFile>? pairedCards = null)
    {
        var folder = Path.GetDirectoryName(project.NativePath!)!;
        var state = Path.Combine(folder, MarkerFolder);
        ShopFileSystem.RequireOrdinaryPath(state);
        if (!WorkflowWindowsFolderName.IsValid(name))
            throw new WorkflowValidationException($"Невозможно сохранить «{name}». Проверьте имя проекта.");
        var output = Path.Combine(folder, name);
        var receiptPath = Path.Combine(state, name);
        var sources = pairedCards is null ? candidates : candidates.Concat(pairedCards.Values).ToArray();
        var inputs = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            sources.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => new { f.Name, f.Length, f.Sha256 }))));
        if ((await ReadOwnedAsync(output, receiptPath, token))?.InputsHash == inputs) return output;

        Directory.CreateDirectory(state);
        WorkflowServiceFiles.TryHide(state);
        var stage = Path.Combine(state, Guid.NewGuid().ToString("N") + ".tmp");
        var stagedReceipt = stage + ".json";
        try
        {
            var match = plan.WordMatch ?? new WordMapMatch([Path.GetFileName(project.NativePath!)], []);
            if (pairedCards is not null) match = match with { RequireGnfPairs = true };
            var built = await WorkflowShopTransfer.BuildMapBundleAsync(candidates, format, stage, word, token, match, pairedCards);
            var gatePath = Path.Combine(state, "merge.lock");
            ShopFileSystem.RequireOrdinaryPath(gatePath);
            using var gate = new FileStream(gatePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await validate(token);
            if (!ShopFileSystem.CandidateNames(folder, [".doc"], hans: pairedCards is null).SequenceEqual(expectedNames))
                throw new WorkflowValidationException("Состав карт изменился. Объедините карты повторно.");
            foreach (var file in sources)
                if (await ShopFileSystem.FingerprintAsync(file.SourcePath, token) != (file.Length, file.Sha256))
                    throw new WorkflowValidationException($"Карта «{file.Name}» изменилась. Объедините карты повторно.");
            var previous = await ReadOwnedAsync(output, receiptPath, token);
            if (previous?.InputsHash == inputs) return output;
            await File.WriteAllTextAsync(stagedReceipt, JsonSerializer.Serialize(new Receipt(new(inputs, built.Sha256), previous)), token);
            token.ThrowIfCancellationRequested();
            // Exclude the name before publishing and keep the previous fingerprint
            // valid if the replacement is interrupted or the file is locked.
            File.Move(stagedReceipt, receiptPath, overwrite: true);
            try { File.Move(stage, output, overwrite: previous is not null); }
            catch
            {
                if (previous is null) TryDelete(receiptPath);
                throw;
            }
            return output;
        }
        finally { TryDelete(stage); TryDelete(stagedReceipt); }
    }

    private static async Task<BundleVersion?> ReadOwnedAsync(string output, string receiptPath, CancellationToken token)
    {
        ShopFileSystem.RequireOrdinaryPath(output);
        if (!File.Exists(output))
        {
            ShopFileSystem.RequireOrdinaryPath(receiptPath);
            return null;
        }
        var receipt = await ReadReceiptAsync(receiptPath, token) ?? throw CannotReplace(output);
        var hash = (await ShopFileSystem.FingerprintAsync(output, token)).Hash;
        return FindVersion(receipt, hash) ?? throw CannotReplace(output);
    }

    internal static async Task<bool> IsRecordedBundleAsync(string receiptPath, string hash, CancellationToken token)
    {
        try { return FindVersion(await ReadReceiptAsync(receiptPath, token), hash) is not null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkflowValidationException) { return false; }
    }

    private static async Task<Receipt?> ReadReceiptAsync(string receiptPath, CancellationToken token)
    {
        ShopFileSystem.RequireOrdinaryPath(receiptPath);
        if (!File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 512)
            return null;
        try { return JsonSerializer.Deserialize<Receipt>(await File.ReadAllTextAsync(receiptPath, token)); }
        catch (JsonException) { return null; }
    }

    private static BundleVersion? FindVersion(Receipt? receipt, string hash) =>
        receipt?.Current?.OutputHash == hash ? receipt.Current :
        receipt?.Previous?.OutputHash == hash ? receipt.Previous : null;

    private static WorkflowValidationException CannotReplace(string output) =>
        new($"Файл «{Path.GetFileName(output)}» уже существует или изменён вручную. Переименуйте его перед объединением.");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
