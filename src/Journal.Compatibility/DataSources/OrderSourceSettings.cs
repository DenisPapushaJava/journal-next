using System.Text.Json;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Access;
using Jornal.Infrastructure.Demo;
using Jornal.Infrastructure.Local;

namespace Jornal.Desktop.DataSources;

public sealed record OrderSourceSettings(int Version = 1, string? DatabasePath = null);
public sealed record OrderSourceSettingsLoad(OrderSourceSettings? Settings, string? Error = null);
public interface IOrderSourceSettingsStore
{
    OrderSourceSettingsLoad Load();
    void Save(OrderSourceSettings settings);
}

public sealed class JsonOrderSourceSettingsStore(string path) : IOrderSourceSettingsStore
{
    public static string DefaultPath => Path.Combine(Path.GetDirectoryName(Customization.JsonUiPreferencesStore.DefaultPath)!, "data-source.json");

    public OrderSourceSettingsLoad Load()
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 32 * 1024) return new(null, "Файл настроек базы слишком большой. Выберите источник заново.");
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != 1 ||
                !document.RootElement.TryGetProperty("DatabasePath", out var databasePath) || databasePath.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                return new(null, "Настройки базы несовместимы с этой версией. Выберите источник заново.");
            stream.Position = 0;
            var settings = JsonSerializer.Deserialize<OrderSourceSettings>(stream);
            if (settings is null || settings.Version != 1 || settings.DatabasePath is not null &&
                (!Path.IsPathFullyQualified(settings.DatabasePath) || !IsSupportedPath(settings.DatabasePath)))
                return new(null, "Настройки базы несовместимы с этой версией. Выберите источник заново.");
            if (settings.DatabasePath is not null) _ = Path.GetFullPath(settings.DatabasePath);
            return new(settings);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(new());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new(null, "Не удалось прочитать настройки базы. Выберите источник заново.");
        }
    }

    private static bool IsSupportedPath(string path) => string.Equals(Path.GetExtension(path), ".mdb", StringComparison.OrdinalIgnoreCase) ||
        OperatingSystem.IsLinux() && LocalJournalStore.IsLocalCopyPath(path);

    public void Save(OrderSourceSettings settings)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".data-source-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record OrderSource(string? Path, IOrderReader Reader, IManualOrderCreator? Creator = null, IOrderBatchEditor? Editor = null,
    string? Error = null, bool IsLocalCopy = false)
{
    public IOrderMaintenance? Maintenance { get; init; }
    public IPriceReader? Prices { get; init; }
    public IPriceEditor? PriceEditor { get; init; }
    public string Description => Error is not null ? "Источник не подключён" : Path is null ?
        "Деможурнал · без сохранения" : IsLocalCopy ? "Локальная тестовая копия" :
            System.IO.Path.GetFileName(Path) + (Creator is not null ? "" : Editor is null ? " · только чтение" : " · статусы и БЛЦБ");
    public string Details => Error ?? Path ?? "Вымышленные заявки. Изменения хранятся в памяти текущего сеанса.";
}

public sealed class OrderSourceManager(IOrderSourceSettingsStore settingsStore, Func<string, IOrderReader>? readerFactory = null,
    Func<string, IOrderBatchEditor?>? editorFactory = null, Func<string, IManualOrderCreator?>? creatorFactory = null)
{
    private readonly DemoOrderStore _demo = new();
    public OrderSource Load()
    {
        var loaded = settingsStore.Load();
        return loaded.Error is not null ? new(null, new UnavailableReader(loaded.Error), Error: loaded.Error) : Create(loaded.Settings?.DatabasePath);
    }
    public OrderSource Create(string? path)
    {
        if (path is null) return new(null, _demo, _demo, _demo) { Maintenance = _demo.Maintenance, Prices = _demo.Prices, PriceEditor = _demo.Prices };
        if (LocalJournalStore.IsLocalCopyPath(path))
        {
            if (!OperatingSystem.IsLinux()) return new(path, new UnavailableReader("Локальные тестовые копии доступны на Linux. На Windows выберите исходную MDB."));
            var local = new LocalJournalStore(path);
            return new(System.IO.Path.GetFullPath(path), local, local, local, IsLocalCopy: true) { Maintenance = local, Prices = local, PriceEditor = local };
        }
        IOrderReader reader = readerFactory?.Invoke(path) ?? new AccessOrderReader(path);
        if (readerFactory is null && OperatingSystem.IsWindows())
            reader = new OrderDurationPreparingReader(reader, AccessHostClient.IsBundled ? new AccessHostClient(path) : new OleDbOrderExtras(path),
                AccessHostClient.IsBundled ? new AccessHostClient(path) : new OleDbOrderAnnotations(path));
        return new(System.IO.Path.GetFullPath(path), reader,
            Creator: creatorFactory is not null ? creatorFactory(path) : readerFactory is null && OperatingSystem.IsWindows()
                ? AccessHostClient.IsBundled ? new AccessHostClient(path) : new OleDbManualOrderCreator(path) : null,
            Editor: editorFactory is not null ? editorFactory(path) : readerFactory is null && OperatingSystem.IsWindows()
                ? AccessHostClient.IsBundled ? new AccessHostClient(path) : new OleDbOrderBatchEditor(path) : null)
            { Prices = readerFactory is null ? new AccessPriceReader(path) : reader as IPriceReader,
                PriceEditor = readerFactory is null && OperatingSystem.IsWindows()
                    ? AccessHostClient.IsBundled ? new AccessHostClient(path) : new OleDbPriceEditor(path) : null,
                Maintenance = readerFactory is null && OperatingSystem.IsWindows()
                ? AccessHostClient.IsBundled ? new AccessHostClient(path) : new OleDbOrderMaintenance(path) : null };
    }
    public void Save(OrderSource source) => settingsStore.Save(new(DatabasePath: source.Path));

    private sealed class UnavailableReader(string error) : IOrderReader
    {
        public Task<IReadOnlyList<OrderSummary>> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<IReadOnlyList<OrderSummary>>(new OrderReadException(error));
    }
}
