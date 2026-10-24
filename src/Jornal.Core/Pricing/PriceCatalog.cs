namespace Jornal.Core.Pricing;

public sealed record PriceProfile(string TableName, string Name, string Usage, bool CanEdit = true, bool IsUsedInCalculation = true);
public sealed record PriceGroup(string Name, IReadOnlyList<PriceProfile> Profiles);

// Привязки к прежней MDB. Каталог не создаёт станки, таблицы или новые правила расчёта.
public static class PriceCatalog
{
    public const string UnusedHansLaser = "PriceHL";
    public static IReadOnlyList<PriceGroup> Groups { get; } = Array.AsReadOnly(new PriceGroup[]
    {
        new("Лазер", [new(PriceTables.Laser, "Основной", "Лазер · HansLaser без ID 1/2")]),
        new("Плазма", [new(PriceTables.Plasma, "Основной", "Плазма")]),
        new("Газо-кислородная", [new(PriceTables.GasOxygen, "Основной", "Газо-кислородная резка")]),
        new("HansLaser", [new(PriceTables.Laser, "Базовый", "Лазер · HansLaser без ID 1/2"),
            new(PriceTables.HansLaserId1, "Нержавеющая сталь · ID 1", "HansLaser · ID 1"),
            new(PriceTables.HansLaserId2, "Цветные металлы · ID 2", "HansLaser · ID 2")]),
        new("Прочие прайсы", [new("PriceLM500", "PriceLM500", "Не используется в расчёте", false, false),
            new(UnusedHansLaser, "PriceHL", "Не используется в расчёте", false, false)])
    });

    public static bool CanEdit(string tableName) => CuttingMachine.IsCustomTable(tableName) || Groups.SelectMany(group => group.Profiles)
        .Any(profile => profile.TableName == tableName && profile.CanEdit);
}
