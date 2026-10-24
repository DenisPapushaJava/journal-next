using Jornal.Core.Pricing;

namespace Jornal.Infrastructure.Access;

public sealed partial class AccessHostClient
{
    public async Task<IReadOnlyList<CuttingMachine>> ReadMachinesAsync(CancellationToken cancellationToken = default) =>
        (await SendAsync(new(1, AccessHostOperation.ReadMachines, System.IO.Path.GetFullPath(path)), cancellationToken)).Machines!;
    public async Task<CuttingMachine> UpdateMachineImportAsync(MachineImportChange change, CancellationToken cancellationToken = default)
    {
        var snapshot = change.Snapshot();
        if (!snapshot.HasChanges) return snapshot.Values;
        return (await SendAsync(new(1, AccessHostOperation.UpdateMachineImport, System.IO.Path.GetFullPath(path), MachineImportChange: snapshot), cancellationToken)).Machine!;
    }
    public async Task<CuttingMachine> CreateMachineAsync(MachineCreation creation, CancellationToken cancellationToken = default)
    {
        var snapshot = creation.Snapshot();
        return (await SendAsync(new(1, AccessHostOperation.CreateMachine, System.IO.Path.GetFullPath(path), MachineCreation: snapshot), cancellationToken)).Machine!;
    }
}
