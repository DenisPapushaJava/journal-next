namespace Jornal.Core.Pricing;

public sealed record PriceExportProfile(string Name, PriceTable Table);
public sealed record PriceExportSheet(string MachineName, IReadOnlyList<PriceExportProfile> Profiles);
public sealed record PriceExportWorkbook(DateTimeOffset ExportedAt, IReadOnlyList<PriceExportSheet> Sheets);

public sealed class PriceExportReadException(string machine, string profile, Exception innerException)
    : Exception("Не удалось прочитать выбранный прайс.", innerException)
{
    public string Machine { get; } = machine;
    public string Profile { get; } = profile;
}

// Только чтение сохранённых тарифов. Общий PriceLL500 читается один раз для всей книги.
public sealed class PriceExportReader(IPriceReader reader)
{
    public async Task<PriceExportWorkbook> ReadAsync(IReadOnlyList<PriceGroup> selected, CancellationToken cancellationToken = default)
    {
        var groups = selected.Select(group => group with { Profiles = group.Profiles.ToArray() }).ToArray();
        if (groups.Length == 0 || groups.Any(group => string.IsNullOrWhiteSpace(group.Name) || group.Profiles.Count == 0 ||
                group.Profiles.Any(profile => !profile.IsUsedInCalculation)))
            throw new ArgumentException("Выберите станки, участвующие в расчёте.", nameof(selected));
        var tables = new Dictionary<string, PriceTable>(StringComparer.Ordinal);
        var sheets = new List<PriceExportSheet>();
        foreach (var group in groups)
        {
            var profiles = new List<PriceExportProfile>();
            foreach (var profile in group.Profiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!tables.TryGetValue(profile.TableName, out var table))
                {
                    try
                    {
                        table = await reader.ReadAsync(profile.TableName, cancellationToken);
                        table.Validate();
                        if (table.Name != profile.TableName) throw new PriceReadException("Получен прайс другого станка.");
                        table = table with { Rates = table.Rates.ToArray() };
                        tables.Add(table.Name, table);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    { throw new PriceExportReadException(group.Name, profile.Name, exception); }
                }
                var heading = profile.TableName switch
                {
                    PriceTables.HansLaserId1 => "Нержавеющая сталь",
                    PriceTables.HansLaserId2 => "Цветные металлы",
                    _ => profile.Name
                };
                profiles.Add(new(heading, table));
            }
            sheets.Add(new(group.Name, profiles));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(DateTimeOffset.Now, sheets);
    }
}
