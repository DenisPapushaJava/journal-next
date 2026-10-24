using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Infrastructure.Local;

public sealed partial class LocalJournalStore
{
    public Task<OrderRecord?> ReadRecordAsync(int id, CancellationToken cancellationToken = default) =>
        DiagnosticReads.ObserveAsync(DiagnosticOperation.OrderRead, DiagnosticReadAction.ReadOrder, _path,
            () => ReadAsync(book => Find(book, id), cancellationToken));

    public Task<OrderRecord> UpdateAsync(OrderEditChange change, CancellationToken cancellationToken = default)
    {
        change.Validate();
        return WriteAsync(book =>
        {
            if (Find(book, change.Expected.Id) != change.Expected) throw Conflict();
            var machine = CuttingMachine.Verify(change.PriceRequest.CuttingType, change.Machine, book.Machines ?? []);
            var table = change.RequiresPriceCalculation ? book.Prices.Single(table => table.Name == CuttingPriceCalculator.SelectTable(change.PriceRequest, machine)) : null;
            var saved = change.Calculate(table, machine);
            return (Replace(book, saved), saved);
        }, cancellationToken);
    }

    public async Task DeleteAsync(OrderRecord expected, CancellationToken cancellationToken = default)
    {
        expected.Validate();
        await WriteAsync(book =>
        {
            if (Find(book, expected.Id) != expected) throw Conflict();
            return (book with { Orders = book.Orders.Where(row => row.Id != expected.Id).ToArray(),
                Records = book.Records?.Where(row => row.Id != expected.Id).ToArray() }, true);
        }, cancellationToken);
    }

    public Task<OrderRecord> RestoreAsync(OrderRecord archived, CancellationToken cancellationToken = default)
    {
        archived.Validate();
        return WriteAsync(book =>
        {
            if (Find(book, archived.Id) is not null) throw Conflict();
            return (Replace(book with { Orders = [.. book.Orders, archived.ToSummary()], LastIssuedId = Math.Max(book.LastIssuedId, archived.Id) }, archived), archived);
        }, cancellationToken);
    }

    private static OrderRecord? Find(LocalJournalFile book, int id) => book.Records?.SingleOrDefault(row => row.Id == id) ??
        (book.Orders.SingleOrDefault(row => row.Id == id) is { } row ? OrderRecord.FromSummary(row) : null);

    private static LocalJournalFile Replace(LocalJournalFile book, OrderRecord saved) => book with
    {
        Orders = book.Orders.Select(row => row.Id == saved.Id ? saved.ToSummary() : row).ToArray(),
        Records = [.. (book.Records ?? []).Where(row => row.Id != saved.Id), saved]
    };

    private static OrderWriteException Conflict() => new(OrderWriteFailure.Conflict,
        "Заявка изменена или её код занят. Данные не заменены. Обновите список (F5). Ввод/архив сохранены.");
}
