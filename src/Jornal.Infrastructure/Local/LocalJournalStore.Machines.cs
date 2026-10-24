using Jornal.Core.Pricing;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Infrastructure.Local;

public sealed partial class LocalJournalStore : IMachineCatalogReader, IMachineCreator, IMachineImportEditor
{
    public Task<CuttingMachine> UpdateMachineImportAsync(MachineImportChange change, CancellationToken cancellationToken = default)
    {
        var snapshot = change.Snapshot();
        if (!snapshot.HasChanges) return Task.FromResult(snapshot.Values);
        return WriteAsync(book =>
        {
            var machines = CuttingMachine.Snapshot(book.Machines ?? []);
            snapshot.Verify(machines.SingleOrDefault(machine => machine.TableName == snapshot.Expected.TableName));
            return (book with { Machines = machines.Select(machine => machine.TableName == snapshot.Expected.TableName ? snapshot.Values : machine).ToArray() }, snapshot.Values);
        }, cancellationToken);
    }
    public Task<IReadOnlyList<CuttingMachine>> ReadMachinesAsync(CancellationToken cancellationToken = default) =>
        DiagnosticReads.ObserveAsync(DiagnosticOperation.PriceRead, DiagnosticReadAction.ReadMachineCatalog, _path,
            () => ReadAsync<IReadOnlyList<CuttingMachine>>(book => CuttingMachine.Snapshot(book.Machines ?? []), cancellationToken));
    public Task<CuttingMachine> CreateMachineAsync(MachineCreation creation, CancellationToken cancellationToken = default)
    {
        var snapshot = creation.Snapshot();
        return WriteAsync(book =>
        {
            if (!(book.Machines ?? []).OrderBy(machine => machine.TableName, StringComparer.Ordinal)
                    .SequenceEqual(snapshot.ExpectedMachines.OrderBy(machine => machine.TableName, StringComparer.Ordinal))) throw MachineCreation.Conflict();
            return (book with { Machines = [.. book.Machines ?? [], snapshot.Machine],
                Prices = [.. book.Prices, new(snapshot.Machine.TableName, true, snapshot.Rates)] }, snapshot.Machine);
        }, cancellationToken);
    }
}
