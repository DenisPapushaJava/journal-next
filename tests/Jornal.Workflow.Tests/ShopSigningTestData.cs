using System.Text;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Tests;

// Managed stand-in for the external Word editor; native DOC layout has separate tests.
internal sealed class ShopWordEditor : IWorkflowWordMapEditor
{
    internal int Calls { get; private set; }
    internal int BundleCalls { get; private set; }
    internal WordMapMatch? LastManagerMatch { get; private set; }
    internal string[] LastPairedSources { get; private set; } = [];
    internal bool FailBundle { get; set; }
    internal Func<CancellationToken, Task>? BeforeBundleAsync { get; init; }
    public Task<WordMapEdit?> PrepareAsync(byte[] source, string projectFileName, string caption, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Calls++;
        const string separator = "\n--TEST-SIGNATURE--\n";
        var body = Encoding.UTF8.GetString(source).Split(separator, StringSplitOptions.None)[0];
        var signed = Encoding.UTF8.GetBytes(body + separator + caption);
        return Task.FromResult<WordMapEdit?>(new(source.SequenceEqual(signed) ? null : signed, 1, 1));
    }

    public async Task BuildBundleAsync(string folder, string[] sourceNames, ShopMapBundle format, CancellationToken token, WordMapMatch? managerProject = null)
    {
        BundleCalls++;
        LastManagerMatch = managerProject;
        LastPairedSources = managerProject?.RequireGnfPairs == true
            ? await Task.WhenAll(sourceNames.Select((_, index) => File.ReadAllTextAsync(Path.Combine(folder, $"paired-{index}.doc"), token)))
            : [];
        if (BeforeBundleAsync is not null) await BeforeBundleAsync(token);
        if (FailBundle) throw new WorkflowValidationException("Word не смог объединить тестовые карты.");
        var parts = new string[sourceNames.Length];
        for (var i = 0; i < parts.Length; i++)
            parts[i] = sourceNames[i] + "\n" + await File.ReadAllTextAsync(Path.Combine(folder, $"input-{i}.doc"), token);
        await File.WriteAllTextAsync(Path.Combine(folder, WorkflowShopTransfer.BundleFileName(format)), string.Join("\n", parts), token);
    }
}

internal static class ShopSigningTestData
{
    internal static void WriteBlockMap(CuttingProject project, string path, string machine = "Лазер", string cnc = "Лист.cnc")
    {
        using var maps = new MapSigningFixture(machine);
        var station = machine == "Лазер" ? "3-Лазер" : "Кристалл 260 + ГАЗ";
        File.Copy(maps.Create(native: Path.GetFileName(project.NativePath), station: station, cnc: cnc), path, overwrite: true);
    }
}
