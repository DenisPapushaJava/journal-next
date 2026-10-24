using System.Data.OleDb;
using System.Runtime.Versioning;
using Jornal.Core.Pricing;

namespace Jornal.Infrastructure.Access;

[SupportedOSPlatform("windows")]
internal sealed class OleDbPriceImpactReader(string path) : IPriceImpactReader
{
    public Task<IReadOnlyList<PriceThicknessUsage>> ReadThicknessUsageAsync(PriceTableChange change, CancellationToken cancellationToken = default)
    {
        var snapshot = change.Snapshot();
        return Task.Run<IReadOnlyList<PriceThicknessUsage>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OleDbAccessConnection.Create(path, writable: false); connection.Open();
            var machines = OleDbMachineCatalog.Read(connection, null, cancellationToken);
            OleDbMachineCatalog.RequireRegistered(connection, null, snapshot.Expected.Name, cancellationToken);
            if (!PriceTableChange.Matches(snapshot.Expected, OleDbPriceEditor.ReadSingle(connection, null, snapshot.Expected.Name)))
                throw PriceTableChange.Conflict();
            using var command = connection.CreateCommand(); command.CommandTimeout = 15;
            command.CommandText = "SELECT [Вид_резки], [Толщина], [Коэффициент] FROM [Orders]";
            using var reader = command.ExecuteReader()!;
            IEnumerable<PriceUsageOrder> Rows()
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return PriceUsageOrder.FromStored(reader[0] as string, reader[1] is DBNull ? null : Convert.ToDecimal(reader[1]), reader[2] as string);
                }
            }
            return PriceThicknessUsage.Count(snapshot, Rows(), machines);
        }, cancellationToken);
    }
}
