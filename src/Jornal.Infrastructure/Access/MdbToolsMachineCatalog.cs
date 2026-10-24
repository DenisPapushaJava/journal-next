using Jornal.Core.Pricing;

namespace Jornal.Infrastructure.Access;

internal static class MdbToolsMachineCatalog
{
    internal static async Task<IReadOnlyList<CuttingMachine>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var export = new MdbToolsTableExport();
        if (!(await export.ListTablesAsync(path, cancellationToken)).Contains(CuttingMachine.CatalogTable, StringComparer.OrdinalIgnoreCase)) return [];
        return await export.ReadAsync(path, CuttingMachine.CatalogTable, (text, token) =>
        {
            using var records = MdbToolsTableExport.ReadCsv(text, token, ',', CuttingMachine.CatalogTable).GetEnumerator();
            if (!records.MoveNext()) throw new PriceReadException("Каталог станков не содержит заголовка.");
            var names = records.Current.Select(field => field.Text).ToArray();
            if (names.Length != 3 || !names.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["Name", "PriceTableName", "SpecificationProfileId"]))
                throw new PriceReadException("Структура каталога станков несовместима.");
            var result = new List<CuttingMachine>();
            while (records.MoveNext())
            {
                if (result.Count >= 128 || records.Current.Count != 3) throw new PriceReadException("Каталог станков некорректен.");
                var row = records.Current; var profile = row[Array.FindIndex(names, name => name.Equals("SpecificationProfileId", StringComparison.OrdinalIgnoreCase))];
                result.Add(new(row[Array.FindIndex(names, name => name.Equals("Name", StringComparison.OrdinalIgnoreCase))].Text, row[Array.FindIndex(names, name => name.Equals("PriceTableName", StringComparison.OrdinalIgnoreCase))].Text,
                    profile is { Quoted: false, Text: "NULL" } ? null : profile.Text));
            }
            return (IReadOnlyList<CuttingMachine>)CuttingMachine.Snapshot(result);
        }, cancellationToken);
    }
}
