using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public static partial class WorkflowShopTransfer
{
    private const string BundleName = "Карты раскроев";
    internal static string BundleFileName(ShopMapBundle format) => BundleName + (format == ShopMapBundle.Pdf ? ".pdf" : ".doc");
    private static readonly StringComparer MapNameOrder =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);

    internal static string BundleFileName(ShopMapBundle format, IReadOnlyList<string> sourceNames)
    {
        var fallback = BundleFileName(format);
        var extension = Path.GetExtension(fallback);
        if (sourceNames.Count == 1)
        {
            var single = Path.GetFileNameWithoutExtension(sourceNames[0]) + extension;
            return WorkflowWindowsFolderName.IsValid(single) ? single : fallback;
        }

        var sheets = new SortedDictionary<int, (string Prefix, string Label)>();
        string? commonPrefix = null;
        foreach (var name in sourceNames)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            var separator = stem.LastIndexOf('-');
            if (separator <= 0) return fallback;
            var prefix = stem[..separator];
            var label = stem[(separator + 1)..];
            if (!int.TryParse(label, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ||
                (commonPrefix is not null && !StringComparer.OrdinalIgnoreCase.Equals(prefix, commonPrefix)) ||
                !sheets.TryAdd(number, (prefix, label)))
                return fallback;
            commonPrefix ??= prefix;
        }
        if (sheets.Count == 0) return fallback;
        var first = sheets.First();
        var last = sheets.Last();
        var suffix = (long)last.Key - first.Key + 1 == sheets.Count
            ? first.Value.Label + "-" + last.Value.Label
            : string.Join(",", sheets.Values.Select(sheet => sheet.Label));
        var result = first.Value.Prefix + "-" + suffix + extension;
        return WorkflowWindowsFolderName.IsValid(result) ? result : fallback;
    }

    private static string? BundleKey(ShopTransferSelection selection, out string? fileName)
    {
        fileName = null;
        if (selection.Project.Machine?.ImportProfile != "hans-pdf/v1" ||
            (selection.MapBundle != ShopMapBundle.Pdf &&
             !(selection.MapBundle == ShopMapBundle.Doc && selection.Files.Count(f => f.Kind == ShopFileKind.Map) > 1)))
            return null;
        fileName = BundleFileName(selection.MapBundle,
            selection.Files.Where(f => f.Kind == ShopFileKind.Map).Select(f => f.Name).ToArray());
        // The Word/PDF bytes can contain creation timestamps. Identify repeat transfers by the verified inputs.
        var source = DesiredTree(selection);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = fileName == BundleFileName(selection.MapBundle) ? 1 : 2,
            Format = selection.MapBundle,
            Files = source.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal),
            Directories = source.Directories.Order(StringComparer.Ordinal)
        })));
    }

    internal static async Task<ShopTreeFile> BuildMapBundleAsync(IEnumerable<ShopFile> files, ShopMapBundle format, string output,
        IWorkflowWordMapEditor? word, CancellationToken token, WordMapMatch? managerProject = null,
        IReadOnlyDictionary<string, ShopFile>? pairedCards = null)
    {
        if (word is null) throw new WorkflowValidationException("Для подготовки карт HansLaser требуется Microsoft Word на Windows.");
        var maps = files.OrderBy(f => f.Name, MapNameOrder).ThenBy(f => f.Name, StringComparer.Ordinal).ToArray();
        var folder = Directory.CreateTempSubdirectory("JournalNext-maps-").FullName;
        try
        {
            for (var i = 0; i < maps.Length; i++)
            {
                var map = maps[i];
                await CopyFileAsync(map.SourcePath, Path.Combine(folder, $"input-{i}.doc"), new(map.Name, map.Length, map.Sha256), token);
                if (pairedCards is not null)
                {
                    var paired = pairedCards[map.Name];
                    await CopyFileAsync(paired.SourcePath, Path.Combine(folder, $"paired-{i}.doc"),
                        new(paired.Name, paired.Length, paired.Sha256), token);
                }
            }
            try { await word.BuildBundleAsync(folder, maps.Select(f => f.Name).ToArray(), format, token, managerProject); }
            catch (WordMapUnavailableException ex) { throw new WorkflowValidationException(ex.Message); }
            var generated = Path.Combine(folder, BundleFileName(format));
            var (length, hash) = await ShopFileSystem.FingerprintAsync(generated, token);
            if (length == 0) throw new WorkflowValidationException("Получен пустой файл карт раскроя HansLaser.");
            var expected = new ShopTreeFile(Path.GetFileName(output), length, hash);
            await CopyFileAsync(generated, output, expected, token);
            return expected;
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task SaveProjectBundleAsync(CuttingProject project, string source, ShopTreeFile file, CancellationToken token)
    {
        var folder = Path.Combine(Path.GetDirectoryName(project.NativePath!)!, BundleName);
        var output = Path.Combine(folder, file.RelativePath);
        ShopFileSystem.RequireOrdinaryPath(output);
        Directory.CreateDirectory(folder);
        if (File.Exists(output) && await ShopFileSystem.FingerprintAsync(output, token) == (file.Length, file.Sha256)) return;
        var temp = Path.Combine(folder, ".journal-next-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await CopyFileAsync(source, temp, file, token);
            token.ThrowIfCancellationRequested();
            File.Move(temp, output, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkflowValidationException($"Не удалось сохранить «{file.RelativePath}» в папке проекта. Закройте файл и повторите передачу.");
        }
        finally
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
