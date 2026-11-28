using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private const string CalculationExchangePending = "Расчёт сохранён в папке обмена. Сервер проверит его после синхронизации Яндекс Диска.";
    [ObservableProperty] private string? _calculationExchangeStatus;

    internal string? CalculationExchangeRoot => _localPreferences.CalculationExchangeRoot;
    public bool ShowCalculationExchange => CalculationExchangeRoot is not null && _store is WorkflowMdbStore;
    public bool ShowCalculationExchangeStatus => ShowCalculationExchange || IsRemoteStorage && CalculationExchangeStatus is not null;
    partial void OnCalculationExchangeStatusChanged(string? value) => OnPropertyChanged(nameof(ShowCalculationExchangeStatus));

    internal async Task<string?> SaveCalculationExchangeRootAsync(string? expected, string? root, CancellationToken token)
    {
        if (Updates.IsApplying) return "Дождитесь завершения обновления.";
        try
        {
            var saved = await Task.Run(() => _preferences.Update(current =>
            {
                if (current.CalculationExchangeRoot == root) return current;
                if (current.CalculationExchangeRoot != expected)
                    throw new WorkflowValidationException("Папка обмена изменена в другом окне. Откройте настройки заново.");
                return current with { CalculationExchangeRoot = root };
            }), token);
            _localPreferences = _localPreferences with { CalculationExchangeRoot = saved.CalculationExchangeRoot };
            CalculationExchangeStatus = null;
            OnPropertyChanged(nameof(ShowCalculationExchange));
            OnPropertyChanged(nameof(ShowCalculationExchangeStatus));
            return null;
        }
        catch (WorkflowValidationException ex) { return ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { return "Не удалось сохранить папку обмена расчётами. Проверьте доступ к настройкам."; }
    }

    internal async Task ExportCalculationDraftsAsync(CancellationToken token)
    {
        if (!CanUse || CalculationExchangeRoot is not { } folder || _connection.RemoteClient is not { } client) return;
        CalculationExchangeStatus = null;
        await RunAsync(async () =>
        {
            var (exported, processed, problems) = await Task.Run(async () =>
            {
                var count = 0;
                string? status = null;
                var errors = new List<string>();
                foreach (var id in client.PendingCalculationOrders(token, refresh: true))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (await WorkflowCalculationExport.ExportAsync(client, id, folder, token) is { } result) status = result;
                        else count++;
                    }
                    catch (WorkflowValidationException ex) { errors.Add(ex.Message); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                    { errors.Add("Файлы черновика или папка обмена заняты либо недоступны."); }
                }
                return (count, status, errors);
            }, token);
            if (exported > 0) CalculationExchangeStatus = $"Пакетов в папке обмена: {exported}. Ожидают загрузки на Диск и принятия в офисе.";
            else if (problems.Count == 0) CalculationExchangeStatus = processed ?? "Нет сохранённых черновиков расчётов.";
            if (problems.Count > 0) Error = $"Не выгружено черновиков: {problems.Count}. {problems[0]}";
        }, "Не удалось выгрузить расчёты. Проверьте папку обмена.");
    }

    private async Task RetryCalculationExchangeAsync(WorkflowOfficeStorageClient client, Guid actor, Func<bool> current, CancellationToken token)
    {
        if (CalculationExchangeRoot is not { } folder) return;
        var status = await Task.Run(async () =>
        {
            if (!Directory.Exists(folder)) return null;
            string? result = null;
            foreach (var id in client.PendingCalculationOrders(token))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var lease = client.LockCalculationDraft(id);
                    if (client.ReadQueuedCalculation(id, actor) is { } draft)
                        result = await WorkflowCalculationExport.ExportDraftAsync(client, draft, folder, token) ?? CalculationExchangePending;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or WorkflowValidationException or OverflowException)
                { /* The saved draft remains in the queue for the next attempt. */ }
            }
            return result;
        }, token);
        if (status is not null && current() && CalculationExchangeRoot == folder) CalculationExchangeStatus = status;
    }

    internal async Task ImportCalculationPackageAsync(string path, CancellationToken token)
    {
        if (!CanUse || IsRemoteStorage || _store is not WorkflowMdbStore store) return;
        if (HasCurrentCardEdits || HasPendingReportEdits)
        {
            Error = "Сохраните или отмените изменения открытой карточки перед принятием расчёта.";
            return;
        }
        CalculationExchangeStatus = null;
        await RunAsync(async () =>
        {
            var actor = ActorId();
            WorkflowCalculationExchange.Result result;
            try { result = await Task.Run(() => WorkflowCalculationExchange.ImportAsync(store, actor, path, token), token); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or EndOfStreamException)
            { throw new WorkflowValidationException("Пакет расчёта повреждён или ещё синхронизируется. Повторите проверку после загрузки."); }
            if (!ReferenceEquals(store, _store) || _cuttingTaskDetached) return;
            _workspace = result.Workspace;
            RefreshOrders(preserveRows: true);
            if (CurrentOrder?.Id == result.Order.Id)
                LoadReportOrder(result.Order, result.Order.Specifications.Select(specification => specification.Id).ToHashSet());
            CalculationExchangeStatus = result.AlreadySaved ? $"Расчёт заявки {result.Order.DisplayNumber} уже в офисной базе." :
                $"Расчёт заявки {result.Order.DisplayNumber} принят в офисную базу.";
            if (result.PublicationProblem is { } problem)
                Error = $"Расчёт записан. {problem} После проверки можно снова принять тот же пакет.";
        }, "Пакет или рабочая папка недоступны. Проверьте синхронизацию и доступ к офисной папке.");
    }
}
