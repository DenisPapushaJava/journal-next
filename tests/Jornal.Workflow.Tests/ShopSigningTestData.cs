using System.Text;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Tests;

// Managed stand-in for the external Word editor; native DOC layout has separate tests.
internal sealed class ShopWordEditor : IWorkflowWordMapEditor
{
    internal int Calls { get; private set; }
    public Task<WordMapEdit?> PrepareAsync(byte[] source, string projectFileName, string caption, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Calls++;
        const string separator = "\n--TEST-SIGNATURE--\n";
        var body = Encoding.UTF8.GetString(source).Split(separator, StringSplitOptions.None)[0];
        var signed = Encoding.UTF8.GetBytes(body + separator + caption);
        return Task.FromResult<WordMapEdit?>(new(source.SequenceEqual(signed) ? null : signed, 1, 1));
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
