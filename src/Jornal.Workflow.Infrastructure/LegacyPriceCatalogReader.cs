using Jornal.Core.Pricing;
using Jornal.Infrastructure.Access;
using Jornal.Infrastructure.Local;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

// Only read operations are invoked; source preparation and write APIs are never called.
public sealed class LegacyPriceCatalogReader : ILegacyPriceCatalogReader
{
    public static string NormalizePath(string? path)
    {
        path = path?.Trim();
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !(Path.GetExtension(path).Equals(".mdb", StringComparison.OrdinalIgnoreCase) ||
              OperatingSystem.IsLinux() && LocalJournalStore.IsLocalCopyPath(path)))
            throw new WorkflowValidationException(OperatingSystem.IsLinux()
                ? "Выберите полный путь к MDB или локальной копии .journal-test.json."
                : "Выберите полный путь к существующей базе .mdb.");
        return Path.GetFullPath(path);
    }

    public Task<LegacyPriceCatalog> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        path = NormalizePath(path);
        if (LocalJournalStore.IsLocalCopyPath(path))
        {
            var local = new LocalJournalStore(path);
            return ReadAsync(local, local, cancellationToken);
        }
        var mdb = new AccessPriceReader(path);
        return ReadAsync(mdb, mdb, cancellationToken);
    }

    public static async Task<LegacyPriceCatalog> ReadAsync(IPriceReader prices, IMachineCatalogReader machines, CancellationToken token = default)
    {
        var custom = CuttingMachine.Snapshot(await machines.ReadMachinesAsync(token));
        var catalog = CuttingMachine.LegacyNames.Select(name => new WorkflowMachine(name,
                name == "HansLaser" ? CuttingMachine.HansProfile : CuttingMachine.VintechProfile) { LegacyKey = "legacy:" + name, LegacyName = name })
            .Concat(custom.Select(m => new WorkflowMachine(m.Name, m.SpecificationProfileId) { LegacyKey = "table:" + m.TableName, LegacyName = m.Name })).ToArray();
        var bindings = new List<(string Machine, string Group, string Table)>
        {
            ("Лазер", "Базовая", PriceTables.Laser), ("Плазма", "Базовая", PriceTables.Plasma),
            ("Газо-кислородная", "Базовая", PriceTables.GasOxygen), ("HansLaser", "Базовая", PriceTables.Laser),
            ("HansLaser", "Нержавеющая сталь", PriceTables.HansLaserId1), ("HansLaser", "Цветные металлы", PriceTables.HansLaserId2)
        };
        bindings.AddRange(custom.Select(m => (m.Name, "Базовая", m.TableName)));
        var tables = new Dictionary<string, PriceTable>(StringComparer.Ordinal);
        var warnings = new List<string>();
        foreach (var table in bindings.Select(b => b.Table).Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var read = await prices.ReadAsync(table, token);
            read.Validate();
            if (read.Name != table) throw new WorkflowValidationException("Ответ источника не соответствует выбранному прайсу.");
            if (read.Rates.Any(r => r.RublesPerMeter is < 0))
                throw new WorkflowValidationException($"Прайс {table} содержит отрицательную цену. Исправьте её в прежнем журнале.");
            tables.Add(table, read);
        }
        var rates = new List<LegacyMeterRate>();
        foreach (var binding in bindings)
        {
            var table = tables[binding.Table];
            var valid = table.HasRow ? table.Rates.Where(r => r.RublesPerMeter.HasValue).ToArray() : [];
            if (valid.Length == 0 || valid.Length != table.Rates.Count)
                warnings.Add($"{binding.Machine} · {binding.Group}: пустые тарифы не загружены.");
            rates.AddRange(valid.Select(r => new LegacyMeterRate(new(binding.Machine, binding.Group, r.ThicknessMillimeters), r.RublesPerMeter!.Value)));
        }
        if (rates.Count == 0) throw new WorkflowValidationException("В базе нет доступных цен за метр. Прежние тарифы нового журнала сохранены.");
        token.ThrowIfCancellationRequested();
        return new(catalog, rates.ToArray(), warnings.ToArray());
    }
}
