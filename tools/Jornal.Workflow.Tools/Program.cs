using System.Globalization;
using System.Text.Json;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

if (args.Length != 4 || args[0] != "prepare-prices" ||
    !decimal.TryParse(args[3], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var pierce) || pierce < 0)
{
    Console.Error.WriteLine("Usage: prepare-prices <legacy.mdb> <workflow.json> <pierce-price>"); return 2;
}
try
{
    var source = LegacyPriceCatalogReader.NormalizePath(args[1]); var target = Path.GetFullPath(args[2]);
    if (source == target || Path.GetExtension(target) != ".json") throw new ArgumentException("Select a separate Workflow JSON workspace.");
    var catalog = await new LegacyPriceCatalogReader().ReadAsync(source);
    var store = new LocalWorkflowWorkspaceStore(target); var before = await store.ReadAsync();
    var prepared = await store.WriteAsync(before.Version, workspace =>
    {
        var imported = LegacyPriceImport.Apply(workspace, source, catalog, DateTimeOffset.UtcNow);
        return WorkflowChanges.UpdateCatalog(imported, imported.Machines, imported.Rates.Select(r => r with { PricePerPierce = pierce }).ToArray());
    }, backupBeforeWrite: true);
    Console.WriteLine(JsonSerializer.Serialize(new
    { Workspace = target, prepared.SchemaVersion, Machines = prepared.Machines.Length, Rates = prepared.Rates.Length, Orders = prepared.Orders.Length, PiercePrice = pierce }));
    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{ Console.Error.WriteLine(ex.Message); return 1; }
