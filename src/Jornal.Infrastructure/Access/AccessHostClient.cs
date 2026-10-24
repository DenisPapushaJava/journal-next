using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jornal.Infrastructure.Diagnostics;
using Jornal.Core.Orders;
using Jornal.Core.Pricing;

namespace Jornal.Infrastructure.Access;

/// <summary>Читает и изменяет MDB через поставляемый x86-процесс, независимо от разрядности Office.</summary>
public sealed partial class AccessHostClient(string path, string? executable = null) : IOrderReader, IOrderBatchEditor, IManualOrderCreator, IManualOrderBatchCreator, IPriceReader, IPriceEditor, IPriceImpactReader, IOrderMaintenance, IOrderDurationInitializer, IOrderAnnotationsInitializer, IMachineCatalogReader, IMachineCreator, IMachineImportEditor
{
    private readonly Action<Process> _start = static process => { process.Start(); };
    private readonly Action<Process> _stop = static process => process.Kill();
    private readonly TimeSpan _timeout = TimeSpan.FromMinutes(2);
    private readonly TimeSpan _cleanupTimeout = TimeSpan.FromSeconds(3);
    internal AccessHostClient(string path, string? executable, Action<Process> start, TimeSpan? timeout = null,
        Action<Process>? stop = null, TimeSpan? cleanupTimeout = null) : this(path, executable)
    {
        _start = start;
        _timeout = timeout ?? _timeout;
        _stop = stop ?? _stop;
        _cleanupTimeout = cleanupTimeout ?? _cleanupTimeout;
    }
    public static string DefaultExecutable => System.IO.Path.Combine(AppContext.BaseDirectory, "AccessHost", "Jornal.AccessHost.exe");
    public static bool IsBundled => OperatingSystem.IsWindows() && File.Exists(DefaultExecutable);

    public async Task PrepareAnnotationsAsync(CancellationToken cancellationToken = default)
    {
        try { await SendAsync(new(1, AccessHostOperation.PrepareAnnotations, System.IO.Path.GetFullPath(path)), cancellationToken); }
        catch (OrderWriteException exception)
        {
            throw new OrderWriteException(OrderWriteFailure.NotSaved,
                "Не удалось подготовить гибку и комментарии. Запрос сохранения заявки не отправлен. Для первой подготовки закройте остальные программы, проверьте права на базу и её папку, затем обновите список (F5).", exception);
        }
    }

    public async Task PrepareCuttingTimeAsync(CancellationToken cancellationToken = default) =>
        _ = await SendAsync(new(1, AccessHostOperation.PrepareCuttingTime, System.IO.Path.GetFullPath(path)), cancellationToken);

    public async Task<OrderRecord?> ReadRecordAsync(int id, CancellationToken cancellationToken = default)
    {
        if (id <= 0) throw new ArgumentOutOfRangeException(nameof(id));
        return await DiagnosticReads.ObserveAsync(DiagnosticOperation.OrderRead, DiagnosticReadAction.ReadOrder, path,
            async () => (await SendAsync(new(1, AccessHostOperation.ReadOrder, System.IO.Path.GetFullPath(path), OrderId: id), cancellationToken)).Record);
    }

    public async Task<OrderRecord> UpdateAsync(OrderEditChange change, CancellationToken cancellationToken = default)
    {
        change.Validate();
        if (change.Values.Annotations is not null || change.Expected.Annotations is not null) await PrepareAnnotationsAsync(cancellationToken);
        return (await SendAsync(new(1, AccessHostOperation.UpdateOrder, System.IO.Path.GetFullPath(path), Edit: change), cancellationToken)).Record!;
    }

    public async Task DeleteAsync(OrderRecord expected, CancellationToken cancellationToken = default)
    {
        expected.Validate();
        if (expected.Annotations is not null) await PrepareAnnotationsAsync(cancellationToken);
        await SendAsync(new(1, AccessHostOperation.DeleteOrder, System.IO.Path.GetFullPath(path), Record: expected), cancellationToken);
    }

    public async Task<OrderRecord> RestoreAsync(OrderRecord archived, CancellationToken cancellationToken = default)
    {
        archived.Validate();
        if (archived.Annotations is not null) await PrepareAnnotationsAsync(cancellationToken);
        return (await SendAsync(new(1, AccessHostOperation.RestoreOrder, System.IO.Path.GetFullPath(path), Record: archived), cancellationToken)).Record!;
    }

    public Task<PriceTable> ReadAsync(string tableName, CancellationToken cancellationToken = default) => ReadPriceTableAsync(tableName, cancellationToken);

    public async Task<PriceTable> SaveAsync(PriceTableChange change, CancellationToken cancellationToken = default)
    {
        var snapshot = change.Snapshot();
        return (await SendAsync(new(1, AccessHostOperation.UpdatePriceTable, System.IO.Path.GetFullPath(path), PriceChange: snapshot), cancellationToken)).PriceTable!;
    }

    public async Task<IReadOnlyList<PriceThicknessUsage>> ReadThicknessUsageAsync(PriceTableChange change, CancellationToken cancellationToken = default)
    {
        var snapshot = change.Snapshot();
        return (await SendAsync(new(1, AccessHostOperation.ReadPriceUsage, System.IO.Path.GetFullPath(path), PriceChange: snapshot), cancellationToken)).PriceUsage!;
    }

    public async Task<OrderSummary> CreateAsync(ManualOrderDraft draft, CancellationToken cancellationToken = default)
    {
        draft.Validate();
        if (draft.HasBending || draft.Comment.Length > 0) await PrepareAnnotationsAsync(cancellationToken);
        var response = await SendAsync(new(1, AccessHostOperation.CreateOrder, System.IO.Path.GetFullPath(path), Draft: draft), cancellationToken);
        return response.Orders![0];
    }

    public async Task<IReadOnlyList<OrderSummary>> CreateBatchAsync(IReadOnlyList<ManualOrderDraft> drafts, CancellationToken cancellationToken = default)
    {
        var snapshot = ManualOrderBatch.Snapshot(drafts);
        if (snapshot.Any(draft => draft.HasBending || draft.Comment.Length > 0)) await PrepareAnnotationsAsync(cancellationToken);
        return (await SendAsync(new(1, AccessHostOperation.CreateOrders, System.IO.Path.GetFullPath(path), Drafts: snapshot), cancellationToken)).Orders!;
    }

    public async Task<IReadOnlyList<OrderSummary>> ReadAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new(1, AccessHostOperation.Read, path), cancellationToken);
        return SupportedRows(response);
    }
    public async Task<IReadOnlyList<OrderSummary>> ReadDayAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var rows = SupportedRows(await SendAsync(new(1, AccessHostOperation.ReadDay, path, OrderDate: date), cancellationToken));
        if (rows.Any(row => row.OrderDate != date) || rows.Select(row => row.Id).Distinct().Count() != rows.Length)
            throw new OrderReadException("Модуль Access вернул неверный период. Обновите список (F5).");
        return rows;
    }
    private static OrderSummary[] SupportedRows(AccessHostResponse response) => response.AnnotationsSupported ? response.Orders! :
        response.Orders!.Select(row => row with { AnnotationsUnavailable = true }).ToArray();

    public async Task<PriceTable> ReadPriceTableAsync(string tableName, CancellationToken cancellationToken = default)
    {
        PriceTables.ValidateName(tableName);
        try
        {
            return (await SendAsync(new(1, AccessHostOperation.ReadPriceTable, System.IO.Path.GetFullPath(path), PriceTableName: tableName), cancellationToken)).PriceTable!;
        }
        catch (OrderReadException exception) { throw new PriceReadException(exception.Message, exception); }
    }

    public Task<IReadOnlyList<OrderSummary>> SetColorAsync(IReadOnlyCollection<OrderSummary> expected, int color,
        CancellationToken cancellationToken = default)
    {
        var change = OrderBatchChange.ForColor(expected, color);
        return SendOrdersAsync(new(1, AccessHostOperation.SetColor, path, change.Expected.ToArray(), color), cancellationToken);
    }

    public Task<IReadOnlyList<OrderSummary>> SetDeliveryNumberAsync(IReadOnlyCollection<OrderSummary> expected, string number,
        CancellationToken cancellationToken = default)
    {
        var change = OrderBatchChange.ForDeliveryNumber(expected, number);
        return SendOrdersAsync(new(1, AccessHostOperation.SetDeliveryNumber, path, change.Expected.ToArray(), DeliveryNumber: change.DeliveryNumber), cancellationToken);
    }

    private async Task<IReadOnlyList<OrderSummary>> SendOrdersAsync(AccessHostRequest request, CancellationToken cancellationToken) =>
        SupportedRows(await SendAsync(request, cancellationToken));

    private async Task<AccessHostResponse> SendAsync(AccessHostRequest request, CancellationToken cancellationToken)
    {
        request = request with { DiagnosticSessionId = AppDiagnostics.Current?.SessionId ?? Guid.NewGuid().ToString("N"), DiagnosticEventId = Guid.NewGuid().ToString("N") };
        cancellationToken.ThrowIfCancellationRequested();
        var write = request.Operation is AccessHostOperation.SetColor or AccessHostOperation.SetDeliveryNumber or AccessHostOperation.CreateOrder or AccessHostOperation.CreateOrders or
            AccessHostOperation.UpdateOrder or AccessHostOperation.DeleteOrder or AccessHostOperation.RestoreOrder or AccessHostOperation.PrepareCuttingTime or AccessHostOperation.UpdatePriceTable or AccessHostOperation.CreateMachine or AccessHostOperation.UpdateMachineImport or AccessHostOperation.PrepareAnnotations;
        var dispatched = false;
        var stage = DiagnosticStage.LaunchAccessHost;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(System.IO.Path.GetFullPath(executable ?? DefaultExecutable))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        Task<AccessHostResponse?>? output = null;
        Task? error = null;
        Task? input = null;
        Task? exit = null;
        Stream? inputPipe = null, outputPipe = null, errorPipe = null;
        try
        {
            _start(process);
            inputPipe = process.StandardInput.BaseStream;
            outputPipe = process.StandardOutput.BaseStream;
            errorPipe = process.StandardError.BaseStream;
            output = ReadResponseAsync(outputPipe, deadline.Token);
            // stderr не участвует в протоколе и не передаётся в диагностику, но канал нельзя оставлять заполненным.
            error = errorPipe.CopyToAsync(Stream.Null, 4096, deadline.Token);
            exit = process.WaitForExitAsync(deadline.Token);
            // После начала отправки записи любой обрыв считается неподтверждённым исходом.
            dispatched = true;
            stage = DiagnosticStage.SendAccessRequest;
            input = WriteRequestAsync(process, request, deadline.Token);
            await AwaitWithReadersAsync(input, output, error).ConfigureAwait(false);
            stage = DiagnosticStage.ReadAccessResponse;
            await AwaitWithReadersAsync(exit, output, error).ConfigureAwait(false);
            if (AccessHostBlockedException.IsExitCode(process.ExitCode))
                throw new AccessHostBlockedException(process.ExitCode);
            var response = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            stage = DiagnosticStage.ValidateAccessResponse;
            if (response is null || response.Protocol != 1 || process.ExitCode != 0)
                throw new InvalidDataException("Некорректный ответ модуля Access.");
            if (response.Diagnostic is { } diagnostic)
            {
                diagnostic.Validate();
                if (response.Error is null || diagnostic.EventId != request.DiagnosticEventId || diagnostic.SessionId != request.DiagnosticSessionId ||
                    diagnostic.Component != DiagnosticComponent.AccessHost || diagnostic.Operation != AccessHostDiagnostics.Operation(request.Operation) ||
                    diagnostic.WriteFailure != response.Failure)
                    throw new InvalidDataException("Некорректная диагностика модуля Access.");
                diagnostic = DiagnosticReads.EnrichHostReport(diagnostic);
                response = response with { Diagnostic = diagnostic };
                AppDiagnostics.Current?.Record(diagnostic);
            }
            if (response.Error is not null)
            {
                if (response.Failure is { } failure && !Enum.IsDefined(failure))
                    throw new InvalidDataException("Неизвестный результат записи.");
                if (response.ChangedQuote is { } quote)
                {
                    if (request.Operation is not (AccessHostOperation.CreateOrder or AccessHostOperation.CreateOrders or AccessHostOperation.UpdateOrder) || response.Failure != OrderWriteFailure.NotSaved ||
                        response.Orders is not null || response.PriceTable is not null || response.Record is not null || response.MaintenanceCompleted is not null)
                        throw new InvalidDataException("Некорректный ответ об изменении прайса.");
                    try
                    {
                        if (request.Operation == AccessHostOperation.CreateOrders && (request.Drafts is null || response.ChangedDraftIndex is not { } index || index < 0 || index >= request.Drafts.Length) ||
                            request.Operation != AccessHostOperation.CreateOrders && response.ChangedDraftIndex is not null)
                            throw new InvalidDataException("Некорректный индекс изменившегося прайса.");
                        var priceRequest = request.Operation == AccessHostOperation.CreateOrders ? request.Drafts![response.ChangedDraftIndex!.Value].PriceRequest :
                            request.Draft?.PriceRequest ?? request.Edit!.PriceRequest;
                        var calculated = CuttingPriceCalculator.Calculate(priceRequest,
                            new(quote.TableName, true, [new(priceRequest.ThicknessMillimeters, quote.ThicknessColumn, quote.RublesPerMeter)]),
                            request.Operation == AccessHostOperation.CreateOrders ? request.Drafts![response.ChangedDraftIndex!.Value].Machine : request.Draft?.Machine ?? request.Edit?.Machine);
                        if (calculated != quote) throw new ArgumentException("Цена не соответствует параметрам.");
                    }
                    catch (Exception exception) when (exception is ArgumentException or PriceCalculationException)
                    { throw new InvalidDataException("Некорректная новая цена в ответе модуля Access.", exception); }
                    if (request.Operation == AccessHostOperation.CreateOrders) throw new OrderBatchPriceChangedException(response.ChangedDraftIndex!.Value, quote);
                    if (request.Operation == AccessHostOperation.UpdateOrder) throw new OrderEditPriceChangedException(quote);
                    throw new OrderPriceChangedException(quote);
                }
                if (response.ChangedDraftIndex is not null) throw new InvalidDataException("Индекс цены без новой цены.");
                if (write || request.Operation == AccessHostOperation.ReadPriceUsage && response.Failure is not null)
                    throw AppDiagnostics.Attach(new OrderWriteException(response.Failure ?? OrderWriteFailure.OutcomeUnknown, response.Error), response.Diagnostic);
                throw AppDiagnostics.Attach(new OrderReadException(response.Error), response.Diagnostic);
            }
            if (response.Failure is not null || response.ChangedQuote is not null || response.ChangedDraftIndex is not null)
                throw new InvalidDataException("Неполный ответ модуля Access.");
            if (request.Operation == AccessHostOperation.ReadPriceUsage)
            {
                if (response.PriceUsage is not { } usage || response.Orders is not null || response.Record is not null || response.PriceTable is not null ||
                    response.Machines is not null || response.Machine is not null || response.MaintenanceCompleted is not null ||
                    response.CuttingTimePrepared is not null || response.AnnotationsPrepared is not null ||
                    usage.Any(item => item is null || item.OrderCount < 0) ||
                    !usage.Select(item => item.ColumnName).SequenceEqual(request.PriceChange!.RenamedRates.Select(pair => pair.Before.ColumnName)))
                    throw new InvalidDataException("Не подтверждено количество заявок для изменения толщин.");
                return response;
            }
            if (response.PriceUsage is not null) throw new InvalidDataException("Количество заявок передано для другой операции.");
            if (request.Operation == AccessHostOperation.PrepareAnnotations)
            {
                if (response.AnnotationsPrepared != true || !response.AnnotationsSupported || response.Orders is not null || response.Record is not null || response.PriceTable is not null || response.MaintenanceCompleted is not null || response.CuttingTimePrepared is not null || response.Machines is not null || response.Machine is not null)
                    throw new InvalidDataException("Подготовка хранения пометок не подтверждена.");
                return response;
            }
            if (response.AnnotationsPrepared is not null) throw new InvalidDataException("Пометки переданы для другой операции.");
            if (request.Operation is AccessHostOperation.ReadMachines or AccessHostOperation.CreateMachine or AccessHostOperation.UpdateMachineImport)
            {
                if (response.Orders is not null || response.Record is not null || response.MaintenanceCompleted is not null || response.CuttingTimePrepared is not null)
                    throw new InvalidDataException("Посторонние данные в ответе каталога.");
                if (request.Operation == AccessHostOperation.ReadMachines)
                {
                    if (response.Machines is null || response.Machine is not null || response.PriceTable is not null) throw new InvalidDataException("Каталог не передан.");
                    try { _ = CuttingMachine.Snapshot(response.Machines); }
                    catch (ArgumentException exception) { throw new InvalidDataException("Некорректный каталог станков.", exception); }
                }
                else if (request.Operation == AccessHostOperation.UpdateMachineImport)
                {
                    if (response.Machine != request.MachineImportChange!.Values || response.Machines is not null || response.PriceTable is not null)
                        throw new InvalidDataException("Изменение импорта станка не подтверждено.");
                }
                else
                {
                    if (response.Machine != request.MachineCreation!.Machine || response.Machines is not null || response.PriceTable is null)
                        throw new InvalidDataException("Создание станка и его тарифы не подтверждены.");
                    try { response.PriceTable.Validate(); }
                    catch (ArgumentException exception) { throw new InvalidDataException("Некорректные тарифы нового станка.", exception); }
                    if (!PriceTableChange.Matches(response.PriceTable, new(request.MachineCreation.Machine.TableName, true, request.MachineCreation.Rates)))
                        throw new InvalidDataException("Тарифы станка отличаются от запроса.");
                }
                return response;
            }
            if (response.Machines is not null || response.Machine is not null) throw new InvalidDataException("Каталог передан для другой операции.");
            if (request.Operation == AccessHostOperation.PrepareCuttingTime)
            {
                if (response.CuttingTimePrepared != true || response.Orders is not null || response.Record is not null || response.PriceTable is not null || response.MaintenanceCompleted is not null)
                    throw new InvalidDataException("Подготовка хранения времени не подтверждена.");
                return response;
            }
            if (response.CuttingTimePrepared is not null) throw new InvalidDataException("Ответ подготовки передан для другой операции.");
            if (request.Operation is AccessHostOperation.ReadOrder or AccessHostOperation.UpdateOrder or AccessHostOperation.DeleteOrder or AccessHostOperation.RestoreOrder)
            {
                if (response.MaintenanceCompleted != true || response.Orders is not null || response.PriceTable is not null)
                    throw new InvalidDataException("Операция с полной записью не подтверждена.");
                if (request.Operation == AccessHostOperation.ReadOrder && response.Record is not null && !response.AnnotationsSupported)
                    throw new InvalidDataException("Модуль Access устарел. Гибка и комментарии не проверены.");
                try { response.Record?.Validate(); }
                catch (OrderWriteException exception) { throw new InvalidDataException("Некорректная полная запись в ответе хоста.", exception); }
                if (request.Operation == AccessHostOperation.ReadOrder && response.Record is { } record && record.Id != request.OrderId ||
                    request.Operation == AccessHostOperation.DeleteOrder && response.Record is not null ||
                    request.Operation == AccessHostOperation.RestoreOrder && response.Record != request.Record ||
                    request.Operation == AccessHostOperation.UpdateOrder && (response.Record is not { } updated || updated != request.Edit!.Values with { Price = updated.Price } ||
                        (request.Edit.RequiresPriceCalculation ? updated.Price is not >= 0 : updated.Price != request.Edit.Values.Price)))
                    throw new InvalidDataException("Поля изменённой/восстановленной заявки не соответствуют запросу.");
                if (request.Operation == AccessHostOperation.UpdateOrder && request.Edit?.ExpectedQuote is { } expectedQuote && response.Record!.Price != expectedQuote.StoredPrice)
                    throw new InvalidDataException("Подтверждённая цена не соответствует просмотренной.");
                return response;
            }
            if (response.Record is not null || response.MaintenanceCompleted is not null)
                throw new InvalidDataException("Ответ полной записи передан для другой операции.");
            if (request.Operation is AccessHostOperation.ReadPriceTable or AccessHostOperation.UpdatePriceTable)
            {
                var expectedName = request.PriceChange?.Expected.Name ?? request.PriceTableName;
                if (response.PriceTable is null || response.Orders is not null || response.PriceTable.Name != expectedName)
                    throw new InvalidDataException("Неполный ответ с прайсом модуля Access.");
                try { response.PriceTable.Validate(); }
                catch (ArgumentException exception) { throw new InvalidDataException("Некорректный прайс в ответе модуля Access.", exception); }
                if (request.Operation == AccessHostOperation.UpdatePriceTable && !PriceTableChange.Matches(response.PriceTable, request.PriceChange!.Values))
                    throw new InvalidDataException("Сохранённые тарифы не соответствуют запросу.");
            }
            else if (response.Orders is null || response.PriceTable is not null)
                throw new InvalidDataException("Неполный ответ модуля Access.");
            if (request.Operation == AccessHostOperation.CreateOrder && (response.Orders!.Length != 1 ||
                response.Orders[0] is not { Id: > 0, StoredCuttingPrice: not null }))
                throw new InvalidDataException("Модуль Access не подтвердил новую заявку.");
            if (response.Orders is { } orders)
            {
                try
                {
                    foreach (var order in orders)
                    {
                        if (order is null) throw new InvalidDataException("Пустая строка в ответе модуля Access.");
                        order.CuttingTime?.Validate(); order.Annotations?.Validate();
                    }
                }
                catch (OrderWriteException exception) { throw new InvalidDataException("Некорректное время в ответе модуля Access.", exception); }
            }
            if (request.Operation == AccessHostOperation.CreateOrder && response.Orders![0] is { } created &&
                created != request.Draft!.ToOrder(created.Id, created.StoredCuttingPrice))
                throw new InvalidDataException("Поля созданной заявки не соответствуют запросу.");
            if (request.Operation == AccessHostOperation.CreateOrders)
            {
                var createdRows = response.Orders!;
                var drafts = request.Drafts!;
                if (createdRows.Length != drafts.Length || createdRows.Select(row => row.Id).Distinct().Count() != drafts.Length)
                    throw new InvalidDataException("Модуль Access не подтвердил весь пакет.");
                for (var index = 0; index < drafts.Length; index++)
                {
                    var row = createdRows[index];
                    if (row.Id <= 0 || row.StoredCuttingPrice is not { } price || price < 0 || row != drafts[index].ToOrder(row.Id, price) ||
                        drafts[index].ExpectedQuote is { } expected && expected.StoredPrice != price)
                        throw new InvalidDataException("Поля созданного пакета не соответствуют запросу.");
                }
            }
            return response;
        }
        catch (OrderReadException) { throw; }
        catch (OrderWriteException) { throw; }
        catch (Exception exception) when (exception is AccessHostBlockedException or IOException or InvalidDataException or System.ComponentModel.Win32Exception or
            InvalidOperationException or JsonException or OperationCanceledException)
        {
            DiagnosticStages.Tag(exception, stage);
            var blocked = exception as AccessHostBlockedException ??
                (exception is System.ComponentModel.Win32Exception native && !dispatched && AccessHostBlockedException.IsLaunchCode(native.NativeErrorCode)
                    ? new AccessHostBlockedException(native.NativeErrorCode, native) : null);
            // При блокировке DLL процесс может завершиться раньше, чем клиент запишет stdin.
            if (blocked is null)
            {
                try
                {
                    if (process.HasExited && AccessHostBlockedException.IsExitCode(process.ExitCode))
                        blocked = new AccessHostBlockedException(process.ExitCode, exception);
                }
                catch (Exception stateError) when (stateError is InvalidOperationException or System.ComponentModel.Win32Exception)
                { /* Состояние процесса недоступно; сохраняем исходный характер отказа. */ }
            }
            if (blocked is not null)
            {
                if (write)
                    throw new OrderWriteException(dispatched ? OrderWriteFailure.OutcomeUnknown : OrderWriteFailure.NotSaved,
                        blocked.Message + (dispatched ? " Сохранение не подтверждено. Изменения могли примениться; обновите список (F5) перед повтором."
                            : " Этот запрос на запись в MDB не отправлен."), blocked);
                throw new OrderReadException(blocked.Message, blocked);
            }
            if (write)
                throw new OrderWriteException(dispatched ? OrderWriteFailure.OutcomeUnknown : OrderWriteFailure.NotSaved,
                    dispatched ? "Модуль Access не подтвердил сохранение. Изменения могли примениться. Обновите список (F5) перед повтором."
                    : "Не удалось запустить модуль Access. Пакет не сохранён. Восстановите установку Journal.", exception);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new OrderReadException("Не удалось получить данные от модуля Access. Проверьте доступ к MDB и восстановите установку Journal при необходимости.", exception);
        }
        finally
        {
            await CleanupAsync(process, deadline, inputPipe, outputPipe, errorPipe, input, output, error, exit).ConfigureAwait(false);
        }
    }
}
