using Jornal.Core.Orders;
using System.Text.Json.Serialization;

namespace Jornal.Core.Pricing;

public interface IPriceEditor
{
    Task<PriceTable> SaveAsync(PriceTableChange change, CancellationToken cancellationToken = default);
}

// Существующие тарифы сопоставляются по позиции. Новые добавляются в конец.
public sealed record PriceTableChange(PriceTable Expected, PriceTable Values)
{
    public PriceThicknessUsage[]? ConfirmedUsage { get; init; }
    [JsonIgnore] public bool HasAdditions => Values.Rates.Count > Expected.Rates.Count;
    [JsonIgnore] public IEnumerable<PriceRate> AddedRates => Values.Rates.Skip(Expected.Rates.Count);
    [JsonIgnore] public IEnumerable<(PriceRate Before, PriceRate After)> RenamedRates => Expected.Rates.Zip(Values.Rates)
        .Where(pair => pair.First.ThicknessMillimeters != pair.Second.ThicknessMillimeters).Select(pair => (pair.First, pair.Second));
    [JsonIgnore] public bool HasRenames => RenamedRates.Any();
    [JsonIgnore] public bool ChangesSchema => HasAdditions || HasRenames;

    public static string ColumnName(decimal thickness) => thickness.ToString("0.############################",
        System.Globalization.CultureInfo.InvariantCulture).Replace('.', ',');

    public PriceTableChange Snapshot()
    {
        ArgumentNullException.ThrowIfNull(Expected);
        ArgumentNullException.ThrowIfNull(Values);
        Expected.Validate(); Values.Validate();
        if (!PriceCatalog.CanEdit(Expected.Name) || Values.Name != Expected.Name || !Expected.HasRow || !Values.HasRow ||
            Expected.Rates.Count == 0 || Values.Rates.Count < Expected.Rates.Count || Values.Rates.Count > 255 ||
            Expected.Rates.Zip(Values.Rates).Any(pair =>
                (pair.First.ThicknessMillimeters == pair.Second.ThicknessMillimeters
                    ? pair.First.ColumnName != pair.Second.ColumnName
                    : pair.Second.ColumnName != ColumnName(pair.Second.ThicknessMillimeters) ||
                      Expected.Rates.Any(old => old.ThicknessMillimeters == pair.Second.ThicknessMillimeters)) ||
                pair.First.RublesPerMeter != pair.Second.RublesPerMeter && pair.Second.RublesPerMeter < 0) ||
            AddedRates.Any(rate => rate.ColumnName != ColumnName(rate.ThicknessMillimeters) || rate.RublesPerMeter is null or < 0))
            throw new ArgumentException("Проверьте толщины и цены: положительная толщина без повторов, целая неотрицательная цена. Удаление тарифов не поддерживается.");
        if (ConfirmedUsage is not null && (ConfirmedUsage.Length != RenamedRates.Count() ||
            ConfirmedUsage.Any(usage => usage is null || usage.OrderCount < 0) ||
            !ConfirmedUsage.Select(usage => usage.ColumnName).SequenceEqual(RenamedRates.Select(pair => pair.Before.ColumnName))))
            throw new ArgumentException("Некорректное подтверждение изменения толщин.");
        return new(Expected with { Rates = Expected.Rates.ToArray() }, Values with { Rates = Values.Rates.ToArray() })
            { ConfirmedUsage = ConfirmedUsage?.ToArray() };
    }

    public void VerifyUsage(IReadOnlyList<PriceThicknessUsage> current)
    {
        if (HasRenames && (ConfirmedUsage is null || !ConfirmedUsage.SequenceEqual(current)))
            throw new OrderWriteException(OrderWriteFailure.NotSaved,
                "Количество заявок изменилось или изменение толщин не подтверждено. Проверьте последствия перед сохранением.");
    }

    public static bool Matches(PriceTable left, PriceTable right) => left.Name == right.Name &&
        left.HasRow == right.HasRow && left.Rates.SequenceEqual(right.Rates);

    public static OrderWriteException Conflict() => new(OrderWriteFailure.Conflict,
        "Прайс уже изменён другим пользователем. Ваш ввод сохранён. Обновите прайс и проверьте правки перед сохранением.");
}
