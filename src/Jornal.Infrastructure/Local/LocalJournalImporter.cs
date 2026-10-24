using System.Security.Cryptography;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Access;

namespace Jornal.Infrastructure.Local;

public sealed class LocalJournalImporter(Func<string, IOrderReader>? orderReaderFactory = null,
    Func<string, IPriceReader>? priceReaderFactory = null)
{
    public async Task ImportAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        _ = new LocalJournalStore(destinationPath);
        if (File.Exists(destinationPath)) throw new IOException("Локальная копия уже существует. Выберите новое имя, чтобы сохранить её изменения.");
        // Экспорт всех таблиц идёт из одного снимка; исходная MDB открывается только для чтения.
        var directory = Path.Combine(Path.GetTempPath(), "journal-import-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, "OrderDB.mdb");
        try
        {
            await AccessDatabaseFile.ValidateAsync(sourcePath, cancellationToken);
            var hash = await Task.Run(async () =>
            {
                await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    81920, FileOptions.Asynchronous);
                await using (var copy = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous))
                    await source.CopyToAsync(copy, cancellationToken);
                source.Position = 0;
                var originalHash = await SHA256.HashDataAsync(source, cancellationToken);
                await using var snapshot = File.OpenRead(temporary);
                var snapshotHash = await SHA256.HashDataAsync(snapshot, cancellationToken);
                if (!originalHash.SequenceEqual(snapshotHash))
                    throw new OrderReadException("MDB изменялась во время копирования. Повторите импорт из отдельной закрытой копии базы.");
                return Convert.ToHexStringLower(originalHash);
            }, cancellationToken);
            var orders = await (orderReaderFactory?.Invoke(temporary) ?? new AccessOrderReader(temporary)).ReadAsync(cancellationToken);
            var reader = priceReaderFactory?.Invoke(temporary) ?? new AccessPriceReader(temporary);
            var prices = new List<PriceTable>();
            var machines = reader is IMachineCatalogReader catalog ? await catalog.ReadMachinesAsync(cancellationToken) : [];
            foreach (var name in PriceTables.All.Concat(machines.Select(machine => machine.TableName))) prices.Add(await reader.ReadAsync(name, cancellationToken));
            await LocalJournalStore.InitializeAsync(destinationPath, orders, prices, Path.GetFileName(sourcePath), hash, cancellationToken, machines);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
