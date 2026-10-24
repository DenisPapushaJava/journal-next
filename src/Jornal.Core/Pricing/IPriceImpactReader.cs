using Jornal.Core.Orders;

namespace Jornal.Core.Pricing;

public interface IPriceImpactReader
{
    Task<IReadOnlyList<PriceThicknessUsage>> ReadThicknessUsageAsync(PriceTableChange change, CancellationToken cancellationToken = default);
}

public sealed record PriceThicknessUsage(string ColumnName, int OrderCount)
{
    public static PriceThicknessUsage[] Count(PriceTableChange change, IEnumerable<PriceUsageOrder> orders, IReadOnlyList<CuttingMachine> machines)
    {
        var counts = change.RenamedRates.ToDictionary(pair => pair.Before.ThicknessMillimeters, _ => 0);
        if (counts.Count == 0) return [];
        foreach (var order in orders)
        {
            if (order.Thickness is not { } thickness || !counts.ContainsKey(thickness)) continue;
            var machine = machines.SingleOrDefault(machine => machine.Name == order.CuttingType);
            if (!CuttingMachine.LegacyNames.Contains(order.CuttingType) && machine is null) continue;
            if (CuttingPriceCalculator.SelectTableForMachine(order.CuttingType, order.MaterialId, machine) == change.Expected.Name)
                counts[thickness] = checked(counts[thickness] + 1);
        }
        return change.RenamedRates.Select(pair => new PriceThicknessUsage(pair.Before.ColumnName, counts[pair.Before.ThicknessMillimeters])).ToArray();
    }
}

public sealed record PriceUsageOrder(string CuttingType, decimal? Thickness, string? MaterialId)
{
    public static PriceUsageOrder From(OrderSummary order) => new(order.CuttingType, order.ThicknessMillimeters, order.MaterialId);
    public static PriceUsageOrder FromStored(string? cuttingType, decimal? thickness, string? coefficient)
    {
        var parts = coefficient?.Split('|', StringSplitOptions.TrimEntries) ?? [];
        return new(cuttingType ?? "", thickness, parts.Length > 3 && parts[3] != "-1" ? parts[3] : "");
    }
}
