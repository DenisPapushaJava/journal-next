namespace Jornal.Workflow.Core;

public sealed record WorkflowMachine(string Name, string? ImportProfile)
{
    public Guid Id { get; init; }
    public string? LegacyKey { get; init; }
    public string? LegacyName { get; init; }
    public string? ProjectExtension { get; init; }
    public static bool SupportsProfile(string? profile) => profile is null or "block-html/v1" or "hans-pdf/v1" or "waterjet-awjc/v1";
    public static WorkflowMachine[] Defaults() => new[]
    {
        new WorkflowMachine("Лазер", "block-html/v1"), new("Плазма", "block-html/v1"), new("Газо-кислородная", "block-html/v1"),
        new("HansLaser", "hans-pdf/v1"), new("ГАР", "waterjet-awjc/v1"), new("Труборез", null)
    }.Select((m, index) => m with
    {
        Id = Guid.Parse($"20000000-0000-0000-0000-{index + 1:000000000000}"), LegacyName = m.Name,
        LegacyKey = index < 4 ? "legacy:" + m.Name : null
    }).ToArray();
}
public sealed record WorkflowRateKey(string Machine, string MaterialGroup, decimal ThicknessMm);
public sealed record LegacyMeterRate(WorkflowRateKey Key, decimal PricePerMeter);
public sealed record LegacyPriceCatalog(WorkflowMachine[] Machines, LegacyMeterRate[] Rates, string[] Warnings);
public sealed record WorkflowPriceSource(string Path, DateTimeOffset LoadedAt, WorkflowRateKey[] ImportedKeys);

public interface ILegacyPriceCatalogReader
{
    Task<LegacyPriceCatalog> ReadAsync(string path, CancellationToken cancellationToken = default);
}

public static class LegacyPriceImport
{
    public static WorkflowRateKey Key(CuttingRate rate) => new(rate.Machine, rate.MaterialGroup, rate.ThicknessMm);

    // Only the explicitly loaded meter prices change; saved calculations keep their snapshots.
    public static WorkflowWorkspace Apply(WorkflowWorkspace workspace, string path, LegacyPriceCatalog catalog, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(path) || catalog.Rates.Length == 0 || catalog.Rates.Length > 20000 ||
            catalog.Machines.Length > 132 || catalog.Machines.Any(m => string.IsNullOrWhiteSpace(m.Name)) ||
            catalog.Machines.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Machines.Length ||
            catalog.Rates.Select(r => r.Key).Distinct().Count() != catalog.Rates.Length ||
            catalog.Rates.Any(r => r.Key.ThicknessMm <= 0 || r.PricePerMeter < 0 || string.IsNullOrWhiteSpace(r.Key.MaterialGroup) ||
                !catalog.Machines.Any(m => m.Name == r.Key.Machine)))
            throw new WorkflowValidationException("Не удалось загрузить корректный каталог тарифов. Прежние цены сохранены.");

        var machines = workspace.Machines.ToList();
        var ids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var source in catalog.Machines)
        {
            var existing = machines.SingleOrDefault(m => source.LegacyKey is not null && m.LegacyKey == source.LegacyKey)
                ?? machines.SingleOrDefault(m => (m.LegacyKey is null || source.LegacyKey is null) && (m.LegacyName ?? m.Name).Equals(source.Name, StringComparison.OrdinalIgnoreCase));
            var machine = existing is null ? source with { Id = Guid.NewGuid(), LegacyName = source.Name } :
                existing with { LegacyKey = source.LegacyKey ?? existing.LegacyKey, LegacyName = source.Name };
            if (existing is null && machines.Any(m => m.Name.Equals(machine.Name, StringComparison.OrdinalIgnoreCase)))
                throw new WorkflowValidationException($"Станок «{machine.Name}» уже существует с другой привязкой. Переименуйте его перед импортом.");
            if (existing is null) machines.Add(machine); else machines[machines.IndexOf(existing)] = machine;
            ids.Add(source.Name, machine.Id);
        }
        var incoming = catalog.Rates.Select(r => (ids[r.Key.Machine], r.Key.MaterialGroup, r.Key.ThicknessMm)).ToHashSet();
        var prior = (workspace.PriceSource?.ImportedKeys ?? []).Select(key =>
            (workspace.Machines.SingleOrDefault(m => (m.LegacyName ?? m.Name) == key.Machine)?.Id ?? Guid.Empty, key.MaterialGroup, key.ThicknessMm)).ToHashSet();
        var rates = workspace.Rates.Where(r => !prior.Contains((r.MachineId, r.MaterialGroup, r.ThicknessMm)) &&
            !incoming.Contains((r.MachineId, r.MaterialGroup, r.ThicknessMm))).ToList();
        foreach (var meter in catalog.Rates)
        {
            var id = ids[meter.Key.Machine]; var name = machines.Single(m => m.Id == id).Name;
            var old = workspace.Rates.SingleOrDefault(r => r.MachineId == id && r.MaterialGroup == meter.Key.MaterialGroup && r.ThicknessMm == meter.Key.ThicknessMm);
            rates.Add(old is not null && old.PricePerMeter == meter.PricePerMeter && old.PricePerPierce is not null ? old with { Machine = name } :
                new(Guid.NewGuid(), name, meter.Key.MaterialGroup, meter.Key.ThicknessMm, meter.PricePerMeter, old?.PricePerPierce ?? 1m) { MachineId = id });
        }
        return workspace with
        {
            Rates = rates.ToArray(), Machines = machines.ToArray(),
            PriceSource = new(path, at, catalog.Rates.Select(r => r.Key).ToArray())
        };
    }
}
