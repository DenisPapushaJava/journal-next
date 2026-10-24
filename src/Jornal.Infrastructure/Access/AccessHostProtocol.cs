using Jornal.Core.Orders;
using Jornal.Core.Pricing;

namespace Jornal.Infrastructure.Access;

public enum AccessHostOperation { Probe, Read, SetColor, SetDeliveryNumber, ReadPriceTable, CreateOrder, ReadOrder, UpdateOrder, DeleteOrder, RestoreOrder, PrepareCuttingTime, CreateOrders, UpdatePriceTable, ReadMachines, CreateMachine, PrepareAnnotations, ReadPriceUsage, ReadDay, UpdateMachineImport }

// Один запрос на процесс. Рабочие пути и данные передаются только по перенаправленному stdin.
public sealed record AccessHostRequest(int Protocol, AccessHostOperation Operation, string Path,
    OrderSummary[]? Expected = null, int Color = 0, string? DeliveryNumber = null, string? PriceTableName = null,
    ManualOrderDraft? Draft = null, int OrderId = 0, OrderRecord? Record = null, OrderEditChange? Edit = null,
    string? DiagnosticSessionId = null, string? DiagnosticEventId = null, ManualOrderDraft[]? Drafts = null, PriceTableChange? PriceChange = null,
    MachineCreation? MachineCreation = null, DateOnly? OrderDate = null, MachineImportChange? MachineImportChange = null);

public sealed record AccessHostResponse(int Protocol, OrderSummary[]? Orders = null,
    string? Error = null, OrderWriteFailure? Failure = null, string? Provider = null, PriceTable? PriceTable = null,
    CuttingPriceQuote? ChangedQuote = null, OrderRecord? Record = null, bool? MaintenanceCompleted = null, bool? CuttingTimePrepared = null,
    Diagnostics.DiagnosticReport? Diagnostic = null, int? ChangedDraftIndex = null, CuttingMachine[]? Machines = null, CuttingMachine? Machine = null, bool? AnnotationsPrepared = null, bool AnnotationsSupported = false,
    PriceThicknessUsage[]? PriceUsage = null);

public static class AccessHostDiagnostics
{
    public static Diagnostics.DiagnosticOperation Operation(AccessHostOperation operation) => operation switch
    {
        AccessHostOperation.CreateOrder or AccessHostOperation.CreateOrders => Diagnostics.DiagnosticOperation.OrderCreate,
        AccessHostOperation.UpdateOrder => Diagnostics.DiagnosticOperation.OrderEdit,
        AccessHostOperation.DeleteOrder => Diagnostics.DiagnosticOperation.OrderDelete,
        AccessHostOperation.RestoreOrder => Diagnostics.DiagnosticOperation.OrderRestore,
        AccessHostOperation.SetColor or AccessHostOperation.SetDeliveryNumber => Diagnostics.DiagnosticOperation.OrderBatch,
        AccessHostOperation.ReadPriceTable => Diagnostics.DiagnosticOperation.PriceRead,
        AccessHostOperation.Read or AccessHostOperation.ReadOrder or AccessHostOperation.ReadDay => Diagnostics.DiagnosticOperation.OrderRead,
        _ => Diagnostics.DiagnosticOperation.AccessHost
    };
}
