using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;
using Jornal.Infrastructure.Access;
using Jornal.Infrastructure.Diagnostics;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
if (!OperatingSystem.IsWindows() || Environment.Is64BitProcess)
{
    Console.Error.WriteLine("Модуль Access должен запускаться как Windows x86.");
    return 2;
}

// Хост использует встроенный Jet, не регистрирует DLL и не устанавливает компоненты Office.
if (args.SequenceEqual(["--probe"]))
{
    try
    {
        var type = Type.GetTypeFromProgID("Microsoft.Jet.OLEDB.4.0", throwOnError: true)!;
        var provider = Activator.CreateInstance(type)!;
        Marshal.FinalReleaseComObject(provider);
        return 0;
    }
    catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 3; }
}
if (args.Length != 0) return 2;

AccessHostRequest? request = null;
AccessHostResponse response;
var diagnosticStage = DiagnosticStage.ValidateAccessRequest;
try
{
    var json = await Console.In.ReadLineAsync() ?? throw new InvalidDataException("Запрос не передан.");
    request = JsonSerializer.Deserialize<AccessHostRequest>(json) ?? throw new InvalidDataException("Пустой запрос.");
    if (request.Protocol != 1 || !Enum.IsDefined(request.Operation) || request.Operation == AccessHostOperation.Probe ||
        !Path.IsPathFullyQualified(request.Path) || !string.Equals(Path.GetExtension(request.Path), ".mdb", StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Несовместимый запрос модуля Access.");
    diagnosticStage = DiagnosticStage.Unknown;
    if (request.Operation == AccessHostOperation.ReadMachines)
        response = new(1, Machines: (await new OleDbMachineCatalog(request.Path).ReadMachinesAsync()).ToArray());
    else if (request.Operation == AccessHostOperation.UpdateMachineImport)
    {
        var change = request.MachineImportChange ?? throw new InvalidDataException("Не передана привязка импорта станка.");
        response = new(1, Machine: await new OleDbMachineCatalog(request.Path).UpdateMachineImportAsync(change));
    }
    else if (request.Operation == AccessHostOperation.CreateMachine)
    {
        var creation = request.MachineCreation ?? throw new InvalidDataException("Не передан новый станок.");
        var machine = await new OleDbMachineCatalog(request.Path).CreateMachineAsync(creation);
        response = new(1, Machine: machine, PriceTable: new(machine.TableName, true, creation.Rates));
    }
    else if (request.Operation == AccessHostOperation.PrepareAnnotations)
    {
        await new OleDbOrderAnnotations(request.Path).PrepareAnnotationsAsync();
        response = new(1, AnnotationsPrepared: true);
    }
    else if (request.Operation == AccessHostOperation.PrepareCuttingTime)
    {
        diagnosticStage = DiagnosticStage.PrepareCuttingTime;
        await new OleDbOrderExtras(request.Path).PrepareCuttingTimeAsync();
        response = new(1, CuttingTimePrepared: true);
    }
    else if (request.Operation == AccessHostOperation.ReadPriceTable)
        response = new(1, PriceTable: await new AccessPriceReader(request.Path).ReadAsync(request.PriceTableName ?? ""));
    else if (request.Operation == AccessHostOperation.UpdatePriceTable)
        response = new(1, PriceTable: await new OleDbPriceEditor(request.Path).SaveAsync(
            request.PriceChange ?? throw new InvalidDataException("Не переданы изменения прайса.")));
    else if (request.Operation == AccessHostOperation.ReadPriceUsage)
        response = new(1, PriceUsage: (await new OleDbPriceEditor(request.Path).ReadThicknessUsageAsync(
            request.PriceChange ?? throw new InvalidDataException("Не переданы изменения толщин."))).ToArray());
    else if (request.Operation is AccessHostOperation.ReadOrder or AccessHostOperation.UpdateOrder or AccessHostOperation.DeleteOrder or AccessHostOperation.RestoreOrder)
    {
        var maintenance = new OleDbOrderMaintenance(request.Path);
        OrderRecord? row = null;
        switch (request.Operation)
        {
            case AccessHostOperation.ReadOrder: row = await maintenance.ReadRecordAsync(request.OrderId); break;
            case AccessHostOperation.UpdateOrder: row = await maintenance.UpdateAsync(request.Edit ?? throw new InvalidDataException("Не переданы правки.")); break;
            case AccessHostOperation.DeleteOrder: await maintenance.DeleteAsync(request.Record ?? throw new InvalidDataException("Не передана исходная запись.")); break;
            case AccessHostOperation.RestoreOrder: row = await maintenance.RestoreAsync(request.Record ?? throw new InvalidDataException("Не передан архив.")); break;
        }
        response = new(1, Record: row, MaintenanceCompleted: true);
    }
    else
    {
        var editor = new OleDbOrderBatchEditor(request.Path);
        var rows = request.Operation switch
        {
            AccessHostOperation.Read => await new AccessOrderReader(request.Path).ReadAsync(),
            AccessHostOperation.ReadDay => await new AccessOrderReader(request.Path).ReadDayAsync(
                request.OrderDate ?? throw new InvalidDataException("Не передана дата журнала.")),
            AccessHostOperation.SetColor => await editor.SetColorAsync(request.Expected ?? [], request.Color),
            AccessHostOperation.SetDeliveryNumber => await editor.SetDeliveryNumberAsync(request.Expected ?? [], request.DeliveryNumber ?? ""),
            AccessHostOperation.CreateOrders => await new OleDbManualOrderCreator(request.Path).CreateBatchAsync(request.Drafts ?? []),
            AccessHostOperation.CreateOrder => [await new OleDbManualOrderCreator(request.Path).CreateAsync(
                request.Draft ?? throw new InvalidDataException("Не переданы поля новой заявки."))],
            _ => throw new InvalidDataException("Неизвестное действие.")
        };
        response = new(1, rows.ToArray());
    }
}
catch (OrderBatchPriceChangedException exception) { response = new(1, Error: exception.Message, Failure: OrderWriteFailure.NotSaved, ChangedQuote: exception.Quote, ChangedDraftIndex: exception.Index); }
catch (OrderPriceChangedException exception) { response = new(1, Error: exception.Message, Failure: OrderWriteFailure.NotSaved, ChangedQuote: exception.Quote); }
catch (OrderEditPriceChangedException exception) { response = new(1, Error: exception.Message, Failure: OrderWriteFailure.NotSaved, ChangedQuote: exception.Quote); }
catch (PriceCalculationException exception) { response = new(1, Error: exception.Message, Failure: OrderWriteFailure.NotSaved); }
catch (OrderWriteException exception) { response = new(1, Error: exception.Message, Failure: exception.Failure, Diagnostic: CaptureHost(exception, exception.Failure)); }
catch (Exception exception)
{
    var failure = request?.Operation is AccessHostOperation.SetColor or AccessHostOperation.SetDeliveryNumber or AccessHostOperation.CreateOrder or AccessHostOperation.CreateOrders or AccessHostOperation.UpdateOrder or AccessHostOperation.DeleteOrder or AccessHostOperation.RestoreOrder or AccessHostOperation.UpdatePriceTable or AccessHostOperation.CreateMachine or AccessHostOperation.UpdateMachineImport
        ? exception is ArgumentException or InvalidDataException ? OrderWriteFailure.NotSaved : OrderWriteFailure.OutcomeUnknown : (OrderWriteFailure?)null;
    response = new(1, Error: exception is OrderReadException or PriceReadException or ArgumentException or InvalidDataException
        ? exception.Message : "Модуль Access не смог выполнить запрос.",
        Failure: failure, Diagnostic: CaptureHost(exception, failure));
}
await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response with { AnnotationsSupported = true }));
return 0;

DiagnosticReport? CaptureHost(Exception exception, OrderWriteFailure? failure)
{
    if (exception is ArgumentException || failure == OrderWriteFailure.Conflict) return null;
    try
    {
        DiagnosticStages.Tag(exception, diagnosticStage);
        var session = Guid.TryParseExact(request?.DiagnosticSessionId, "N", out var sessionId) ? sessionId.ToString("N") : Guid.NewGuid().ToString("N");
        var id = Guid.TryParseExact(request?.DiagnosticEventId, "N", out var eventId) ? eventId.ToString("N") : Guid.NewGuid().ToString("N");
        return DiagnosticReport.Create(exception, AccessHostDiagnostics.Operation(request?.Operation ?? AccessHostOperation.Probe), session,
            AppDiagnostics.AppVersion, component: DiagnosticComponent.AccessHost, eventId: id) with { WriteFailure = failure };
    }
    catch (Exception) { return null; }
}
