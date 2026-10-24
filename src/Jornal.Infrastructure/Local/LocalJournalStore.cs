using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Infrastructure.Local;

// Отдельная локальная копия для разработки. Этот адаптер никогда не открывает MDB для записи.
public sealed partial class LocalJournalStore : IOrderReader, IManualOrderCreator, IOrderBatchEditor, IManualOrderBatchCreator, IPriceReader, IOrderMaintenance
{
    public const string Extension = ".journal-test.json";
    private const long MaximumSize = 128 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _path;
    private readonly TimeSpan _lockWaitTimeout;

    public LocalJournalStore(string path) : this(path, TimeSpan.FromSeconds(10)) { }

    internal LocalJournalStore(string path, TimeSpan lockWaitTimeout)
    {
        if (!IsLocalCopyPath(path)) throw new ArgumentException("Выберите файл локальной копии " + Extension + ".", nameof(path));
        if (lockWaitTimeout < TimeSpan.Zero || lockWaitTimeout > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(lockWaitTimeout));
        _path = Path.GetFullPath(path);
        _lockWaitTimeout = lockWaitTimeout;
    }

    public static bool IsLocalCopyPath(string? path) => path?.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) == true;

    public Task<IReadOnlyList<OrderSummary>> ReadAsync(CancellationToken cancellationToken = default) =>
        DiagnosticReads.ObserveAsync(DiagnosticOperation.OrderRead, DiagnosticReadAction.ReadOrders, _path,
            () => ReadAsync<IReadOnlyList<OrderSummary>>(book => book.Orders.OrderByDescending(row => row.OrderDate)
                .ThenByDescending(row => row.Id).ToArray(), cancellationToken));

    public Task<PriceTable> ReadAsync(string tableName, CancellationToken cancellationToken = default)
    {
        PriceTables.ValidateName(tableName);
        return DiagnosticReads.ObserveAsync(DiagnosticOperation.PriceRead, DiagnosticReadAction.ReadPriceTable, _path,
            () => ReadAsync(book => book.Prices.SingleOrDefault(table => table.Name == tableName) ??
                throw new PriceReadException("В локальной копии этого прайса нет."), cancellationToken));
    }

    public Task<OrderSummary> CreateAsync(ManualOrderDraft draft, CancellationToken cancellationToken = default) =>
        WriteAsync(book =>
        {
            draft.Validate();
            var machine = CuttingMachine.Verify(draft.CuttingType, draft.Machine, book.Machines ?? []);
            var table = book.Prices.Single(price => price.Name == CuttingPriceCalculator.SelectTable(draft.PriceRequest, machine));
            var quote = draft.Calculate(table, machine);
            if (book.LastIssuedId == int.MaxValue)
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "Закончился диапазон номеров локальных записей.");
            var order = draft.ToOrder(book.LastIssuedId + 1, quote.StoredPrice);
            return (book with { LastIssuedId = order.Id, Orders = [.. book.Orders, order] }, order);
        }, cancellationToken);

    public Task<IReadOnlyList<OrderSummary>> CreateBatchAsync(IReadOnlyList<ManualOrderDraft> drafts, CancellationToken cancellationToken = default)
    {
        var snapshot = ManualOrderBatch.Snapshot(drafts);
        return WriteAsync<IReadOnlyList<OrderSummary>>(book =>
        {
            if ((long)book.LastIssuedId + snapshot.Length > int.MaxValue)
                throw new OrderWriteException(OrderWriteFailure.NotSaved, "Закончился диапазон номеров локальных записей.");
            var rows = snapshot.Select((draft, index) =>
            {
                var machine = CuttingMachine.Verify(draft.CuttingType, draft.Machine, book.Machines ?? []);
                var table = book.Prices.Single(price => price.Name == CuttingPriceCalculator.SelectTable(draft.PriceRequest, machine));
                try { return draft.ToOrder(book.LastIssuedId + index + 1, draft.Calculate(table, machine).StoredPrice); }
                catch (OrderPriceChangedException changed) { throw new OrderBatchPriceChangedException(index, changed.Quote); }
            }).ToArray();
            return (book with { LastIssuedId = rows[^1].Id, Orders = [.. book.Orders, .. rows] }, rows);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<OrderSummary>> SetColorAsync(IReadOnlyCollection<OrderSummary> expected, int color,
        CancellationToken cancellationToken = default) => UpdateAsync(OrderBatchChange.ForColor(expected, color), cancellationToken);

    public Task<IReadOnlyList<OrderSummary>> SetDeliveryNumberAsync(IReadOnlyCollection<OrderSummary> expected, string number,
        CancellationToken cancellationToken = default) => UpdateAsync(OrderBatchChange.ForDeliveryNumber(expected, number), cancellationToken);

    private Task<IReadOnlyList<OrderSummary>> UpdateAsync(OrderBatchChange change, CancellationToken cancellationToken) =>
        WriteAsync<IReadOnlyList<OrderSummary>>(book =>
        {
            var current = book.Orders.ToDictionary(row => row.Id);
            if (change.Expected.Any(expected => !current.TryGetValue(expected.Id, out var row) || !change.Matches(row, expected)))
                throw OrderWriteException.Conflict();
            var replacements = change.Expected.ToDictionary(row => row.Id, row => change.Apply(current[row.Id]));
            return (book with { Orders = book.Orders.Select(row => replacements.GetValueOrDefault(row.Id, row)).ToArray(),
                Records = book.Records?.Select(record => replacements.TryGetValue(record.Id, out var updated)
                    ? record with { Color = updated.Color, DeliveryNumber = updated.DeliveryNumber } : record).ToArray() },
                replacements.Values.ToArray());
        }, cancellationToken);

    public static Task InitializeAsync(string path, IReadOnlyList<OrderSummary> orders, IReadOnlyList<PriceTable> prices,
        string sourceName, string sourceSha256, CancellationToken cancellationToken = default, IReadOnlyList<CuttingMachine>? machines = null) => Task.Run(() =>
        {
            var store = new LocalJournalStore(path);
            var book = new LocalJournalFile("Journal.LocalTestCopy", 1, sourceName, sourceSha256, DateTimeOffset.UtcNow,
                Math.Max(0, orders.Select(row => row.Id).DefaultIfEmpty().Max()), orders.ToArray(), prices.ToArray(), Machines: machines?.ToArray());
            Validate(book);
            Directory.CreateDirectory(Path.GetDirectoryName(store._path)!);
            using var gate = store.Acquire(cancellationToken);
            // Повторный импорт всегда создаёт новый файл, а не стирает сделанные пользователем изменения.
            store.Save(book, overwrite: false, cancellationToken);
        }, cancellationToken);

    private Task<T> ReadAsync<T>(Func<LocalJournalFile, T> read, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            using var gate = Acquire(cancellationToken);
            var result = read(Load());
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (LocalJournalBusyException exception)
        {
            throw new OrderReadException(exception.Message, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OrderReadException(DataSourceAccessErrors.Describe(_path, exception,
                "Локальная копия недоступна. Проверьте путь и права доступа."), exception);
        }
    }, cancellationToken);

    private Task<T> WriteAsync<T>(Func<LocalJournalFile, (LocalJournalFile Book, T Result)> update,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            using var gate = Acquire(cancellationToken);
            var (book, result) = update(Load());
            Save(book, overwrite: true, cancellationToken);
            return result;
        }
        catch (LocalJournalBusyException exception)
        {
            throw new OrderWriteException(OrderWriteFailure.NotSaved,
                "Локальная база сейчас занята другой операцией. Изменения не сохранены. Повторите действие через несколько секунд.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OrderReadException)
        {
            throw new OrderWriteException(OrderWriteFailure.NotSaved,
                DataSourceAccessErrors.Describe(_path, exception,
                    "Не удалось сохранить локальную копию. Ввод сохранён; проверьте файл и повторите действие."), exception);
        }
    }, cancellationToken);

    private LocalJournalFile Load()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaximumSize) throw new OrderReadException("Локальная копия превышает допустимые 128 МиБ.");
        try
        {
            var book = JsonSerializer.Deserialize<LocalJournalFile>(stream, JsonOptions);
            if (book is null) throw new JsonException();
            Validate(book);
            return book;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or OrderWriteException or OverflowException)
        {
            throw new OrderReadException("Локальная копия повреждена или имеет неподдерживаемый формат. Файл не изменён.", exception);
        }
    }

    private void Save(LocalJournalFile book, bool overwrite, CancellationToken cancellationToken)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = OpenPrivateFile(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, book, JsonOptions);
                if (stream.Length > MaximumSize) throw new IOException("Локальная копия превышает допустимые 128 МиБ.");
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private FileStream Acquire(CancellationToken cancellationToken)
    {
        // Все экземпляры и процессы приложения используют один lock-файл. Удалять его после закрытия нельзя:
        // другой процесс может уже открыть тот же inode; перенос JSON выполняется под этой блокировкой.
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return OpenPrivateFile(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite); }
            catch (IOException exception) when (LocalJournalBusyException.IsSharingViolation(exception))
            {
                if (timer.Elapsed >= _lockWaitTimeout) throw new LocalJournalBusyException(exception);
                if (cancellationToken.WaitHandle.WaitOne(25)) cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static FileStream OpenPrivateFile(string path, FileMode mode, FileAccess access)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = FileShare.None };
        if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static void Validate(LocalJournalFile book)
    {
        _ = CuttingMachine.Snapshot(book.Machines ?? []);
        if (book.Format != "Journal.LocalTestCopy" || book.Version != 1 || string.IsNullOrEmpty(book.SourceName) ||
            book.SourceSha256 is not { Length: 64 } || !book.SourceSha256.All(Uri.IsHexDigit) ||
            book.Orders is null || book.Prices is null || book.LastIssuedId < 0 ||
            book.Orders.Any(row => row is null || row.Id > book.LastIssuedId || row.SpecificationNumber is null ||
                row.ManagerName is null || row.CustomerName is null || row.MaterialName is null || row.CuttingType is null) ||
            book.Orders.Select(row => row.Id).Distinct().Count() != book.Orders.Length ||
            book.Prices.Length != PriceTables.All.Count + (book.Machines?.Length ?? 0) ||
            !book.Prices.Select(table => table?.Name).ToHashSet().SetEquals(PriceTables.All.Concat((book.Machines ?? []).Select(machine => machine.TableName))))
            throw new ArgumentException("Некорректный файл локальной копии.");
        if (book.Records is { } records)
        {
            var orders = book.Orders.ToDictionary(row => row.Id);
            if (records.Any(row => row is null) || records.Select(row => row.Id).Distinct().Count() != records.Length)
                throw new ArgumentException("Некорректные исходные записи локальной копии.");
            foreach (var record in records)
            {
                record.Validate();
                if (!orders.TryGetValue(record.Id, out var row) || row != record.ToSummary())
                    throw new ArgumentException("Исходные записи не соответствуют списку локальной копии.");
            }
        }
        foreach (var row in book.Orders)
        {
            row.CuttingTime?.Validate(); row.Annotations?.Validate();
            if (row.AnnotationsUnavailable) throw new ArgumentException("Пометки исходной заявки недоступны.");
        }
        foreach (var table in book.Prices) table.Validate();
    }

}

internal sealed record LocalJournalFile(string Format, int Version, string SourceName, string SourceSha256,
    DateTimeOffset ImportedAt, int LastIssuedId, OrderSummary[] Orders, PriceTable[] Prices, OrderRecord[]? Records = null, CuttingMachine[]? Machines = null);
