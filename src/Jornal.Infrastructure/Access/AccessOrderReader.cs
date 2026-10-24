using Jornal.Core.Orders;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Infrastructure.Access;

public sealed class AccessOrderReader(string path) : IOrderReader
{
    public Task<IReadOnlyList<OrderSummary>> ReadAsync(CancellationToken cancellationToken = default) =>
        // Само открытие сетевого файла тоже может ждать SMB, поэтому оно не выполняется в потоке UI.
        DiagnosticReads.ObserveAsync(DiagnosticOperation.OrderRead, DiagnosticReadAction.ReadOrders, path,
            () => Task.Run(() => ReadCoreAsync(null, cancellationToken), cancellationToken));

    public Task<IReadOnlyList<OrderSummary>> ReadDayAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        DiagnosticReads.ObserveAsync(DiagnosticOperation.OrderRead, DiagnosticReadAction.ReadDay, path,
            () => Task.Run(() => ReadCoreAsync(date, cancellationToken), cancellationToken));

    private async Task<IReadOnlyList<OrderSummary>> ReadCoreAsync(DateOnly? date, CancellationToken cancellationToken)
    {
        try
        {
            await AccessDatabaseFile.ValidateAsync(path, cancellationToken);
            IReadOnlyList<OrderSummary> orders;
            if (AccessHostClient.IsBundled) orders = date is { } hostDay
                ? await new AccessHostClient(path).ReadDayAsync(hostDay, cancellationToken) : await new AccessHostClient(path).ReadAsync(cancellationToken);
            else if (OperatingSystem.IsWindows()) orders = date is { } nativeDay
                ? await OleDbOrderReader.ReadDayAsync(path, nativeDay, cancellationToken) : await OleDbOrderReader.ReadAsync(path, cancellationToken);
            else if (OperatingSystem.IsLinux()) orders = await new MdbToolsOrderReader().ReadAsync(path, cancellationToken);
            else throw new OrderReadException("Чтение MDB реализовано для Windows и Linux.");
            if (orders.Select(order => order.Id).Distinct().Count() != orders.Count)
                throw new OrderReadException("В Orders обнаружены повторяющиеся значения поля «Код». Список не загружен.");
            return orders.Where(order => date is null || order.OrderDate == date)
                .OrderByDescending(order => order.OrderDate).ThenByDescending(order => order.Id).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new OrderReadException(DataSourceAccessErrors.Describe(path, exception,
                DataSourceAccessErrors.Unavailable(path)), exception);
        }
    }
}
