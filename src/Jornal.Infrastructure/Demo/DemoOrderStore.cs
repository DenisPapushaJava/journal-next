using Jornal.Core.Orders;
using Jornal.Core.Pricing;

namespace Jornal.Infrastructure.Demo;

public sealed partial class DemoOrderStore : IOrderReader, IManualOrderCreator, IOrderBatchEditor, IManualOrderBatchCreator, IMachineCatalogReader
{
    public DemoOrderStore() => Prices = new(this);
    public PriceAccess Prices { get; }
    private readonly object _gate = new();
    private int _lastIssuedId = 3;
    public IOrderMaintenance Maintenance => new MaintenanceAdapter(this);
    private readonly Dictionary<string, PriceTable> _prices = PriceTables.All.Append(PriceCatalog.UnusedHansLaser)
        .ToDictionary(name => name, name => new PriceTable(name, true,
            new decimal[] { 0.5m, 1, 1.5m, 2, 2.5m, 3, 4, 6, 8, 10, 12, 16, 20, 32, 60, 200 }
                .Select(t => new PriceRate(t, t.ToString(System.Globalization.CultureInfo.InvariantCulture), 25)).ToArray()));

    public Task<PriceTable> ReadAsync(string tableName, CancellationToken cancellationToken = default)
    {
        PriceTables.ValidateName(tableName);
        lock (_gate) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(_prices[tableName]); }
    }

    public Task<PriceTable> SaveAsync(PriceTableChange change, CancellationToken cancellationToken = default)
    {
        var snapshot = change.Snapshot();
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PriceTableChange.Matches(_prices[snapshot.Expected.Name], snapshot.Expected)) throw PriceTableChange.Conflict();
            snapshot.VerifyUsage(PriceThicknessUsage.Count(snapshot, _orders.Select(PriceUsageOrder.From), _machines));
            _prices[snapshot.Expected.Name] = snapshot.Values; return Task.FromResult(snapshot.Values);
        }
    }
    // Только память текущего сеанса. Рабочая MDB здесь никогда не открывается.
    private readonly List<OrderSummary> _orders =
        [
            new(1, new DateOnly(2026, 10, 15), "1015-1001", "Менеджер 1", "Пример [А]",
                "Лазер", "Ст-3", 2.5m, 1250m, -882036)
                { SheetSize = "1250x2500", SheetCount = "2", CutLengthMeters = 48, WeightKilograms = "38.5", DeliveryNumber = "БЛЦБ-00010001" },
            new(2, new DateOnly(2026, 10, 15), "1015-1002", "Менеджер 2", "Пример Б",
                "HansLaser", "5083H111", 6m, 4850m, -10238087)
                { SheetSize = "1500x3000", SheetCount = "3", CutLengthMeters = 127, WeightKilograms = "218.4", DeliveryNumber = "БЛЦБ-00010002" },
            new(3, new DateOnly(2026, 10, 14), "1014-1003", "Менеджер 1", "Пример В",
                "Плазма", "09Г2С", 16m, 3250m, -1)
                { SheetSize = "1500x6000", SheetCount = "1", CutLengthMeters = 35, WeightKilograms = "156.0" }
        ];

    public Task<IReadOnlyList<OrderSummary>> ReadAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<OrderSummary>>(_orders.ToArray());
        }
    }

    public Task<OrderSummary> CreateAsync(ManualOrderDraft draft, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            draft.Validate();
            var machine = CuttingMachine.Verify(draft.CuttingType, draft.Machine, _machines);
            var price = machine is null ? (decimal?)null : draft.Calculate(_prices[machine.TableName], machine).StoredPrice;
            var order = draft.ToOrder(++_lastIssuedId, price);
            _orders.Insert(0, order);
            return Task.FromResult(order);
        }
    }

    public Task<IReadOnlyList<OrderSummary>> CreateBatchAsync(IReadOnlyList<ManualOrderDraft> drafts, CancellationToken cancellationToken = default)
    {
        var snapshot = ManualOrderBatch.Snapshot(drafts);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = snapshot.Select((draft, index) =>
            {
                var machine = CuttingMachine.Verify(draft.CuttingType, draft.Machine, _machines);
                return draft.ToOrder(checked(_lastIssuedId + index + 1), machine is null ? null : draft.Calculate(_prices[machine.TableName], machine).StoredPrice);
            }).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            _orders.InsertRange(0, rows);
            _lastIssuedId = rows[^1].Id;
            return Task.FromResult<IReadOnlyList<OrderSummary>>(rows);
        }
    }

    public Task<IReadOnlyList<OrderSummary>> SetColorAsync(IReadOnlyCollection<OrderSummary> expected, int color,
        CancellationToken cancellationToken = default) => Update(OrderBatchChange.ForColor(expected, color), cancellationToken);

    public Task<IReadOnlyList<OrderSummary>> SetDeliveryNumberAsync(IReadOnlyCollection<OrderSummary> expected, string number,
        CancellationToken cancellationToken = default) => Update(OrderBatchChange.ForDeliveryNumber(expected, number), cancellationToken);

    private Task<IReadOnlyList<OrderSummary>> Update(OrderBatchChange change, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var unique = change.Expected.Select(row => row.Id).ToHashSet();
            var matches = _orders.Select((order, index) => (order, index)).Where(pair => unique.Contains(pair.order.Id)).ToArray();
            var current = matches.ToDictionary(pair => pair.order.Id, pair => pair.order);
            if (matches.Length != unique.Count || change.Expected.Any(expected =>
                !change.Matches(current[expected.Id], expected)))
                throw OrderWriteException.Conflict();
            // Сначала проверяем и подготавливаем весь пакет, затем заменяем записи без промежуточных await.
            var prepared = matches.Select(pair => (Order: change.Apply(pair.order), Index: pair.index)).ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var pair in prepared) _orders[pair.Index] = pair.Order;
            return Task.FromResult<IReadOnlyList<OrderSummary>>(prepared.Select(pair => pair.Order).ToArray());
        }
    }

    // Чтение/правка тарифов отдельно от создания демозаявок с исторически неизвестной ценой.
    public sealed class PriceAccess(DemoOrderStore store) : IPriceReader, IPriceEditor, IMachineCatalogReader, IMachineCreator, IMachineImportEditor, IPriceImpactReader
    {
        public Task<IReadOnlyList<PriceThicknessUsage>> ReadThicknessUsageAsync(PriceTableChange change, CancellationToken cancellationToken = default)
        {
            var snapshot = change.Snapshot();
            lock (store._gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!PriceTableChange.Matches(store._prices[snapshot.Expected.Name], snapshot.Expected)) throw PriceTableChange.Conflict();
                return Task.FromResult<IReadOnlyList<PriceThicknessUsage>>(PriceThicknessUsage.Count(snapshot, store._orders.Select(PriceUsageOrder.From), store._machines));
            }
        }
        public Task<IReadOnlyList<CuttingMachine>> ReadMachinesAsync(CancellationToken cancellationToken = default) => store.ReadMachinesAsync(cancellationToken);
        public Task<CuttingMachine> CreateMachineAsync(MachineCreation creation, CancellationToken cancellationToken = default) => store.CreateMachineAsync(creation, cancellationToken);
        public Task<CuttingMachine> UpdateMachineImportAsync(MachineImportChange change, CancellationToken cancellationToken = default) => store.UpdateMachineImportAsync(change, cancellationToken);
        public Task<PriceTable> ReadAsync(string tableName, CancellationToken cancellationToken = default) => store.ReadAsync(tableName, cancellationToken);
        public Task<PriceTable> SaveAsync(PriceTableChange change, CancellationToken cancellationToken = default) => store.SaveAsync(change, cancellationToken);
    }

    private sealed class MaintenanceAdapter(DemoOrderStore store) : IOrderMaintenance, IMachineCatalogReader
    {
        public Task<IReadOnlyList<CuttingMachine>> ReadMachinesAsync(CancellationToken cancellationToken = default) => store.ReadMachinesAsync(cancellationToken);
        private OrderRecord? Find(int id) => store._orders.SingleOrDefault(row => row.Id == id) is { } row ? OrderRecord.FromSummary(row) : null;
        public Task<OrderRecord?> ReadRecordAsync(int id, CancellationToken cancellationToken = default)
        { lock (store._gate) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Find(id)); } }
        public Task<PriceTable> ReadAsync(string tableName, CancellationToken cancellationToken = default) => store.ReadAsync(tableName, cancellationToken);
        public async Task<OrderRecord> UpdateAsync(OrderEditChange change, CancellationToken cancellationToken = default)
        {
            change.Validate(); var machine = await CuttingPriceService.ResolveMachineAsync(this, change.PriceRequest.CuttingType, cancellationToken);
            var table = change.RequiresPriceCalculation ? await ReadAsync(CuttingPriceCalculator.SelectTable(change.PriceRequest, machine), cancellationToken) : null;
            lock (store._gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Find(change.Expected.Id) != change.Expected) throw OrderWriteException.Conflict();
                CuttingMachine.Verify(change.PriceRequest.CuttingType, change.Machine, store._machines);
                var saved = change.Calculate(table, machine);
                store._orders[store._orders.FindIndex(row => row.Id == saved.Id)] = saved.ToSummary();
                return saved;
            }
        }
        public Task DeleteAsync(OrderRecord expected, CancellationToken cancellationToken = default)
        {
            lock (store._gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Find(expected.Id) != expected) throw OrderWriteException.Conflict();
                store._orders.RemoveAll(row => row.Id == expected.Id); return Task.CompletedTask;
            }
        }
        public Task<OrderRecord> RestoreAsync(OrderRecord archived, CancellationToken cancellationToken = default)
        {
            archived.Validate(); lock (store._gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Find(archived.Id) is not null) throw OrderWriteException.Conflict();
                store._orders.Add(archived.ToSummary()); store._lastIssuedId = Math.Max(store._lastIssuedId, archived.Id);
                return Task.FromResult(archived);
            }
        }
    }
}
