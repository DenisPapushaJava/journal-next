using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Core.Orders;
using Jornal.Desktop.DataSources;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private OrderSource? _source;
    public string SourceDescription => _source?.Description ?? "Деможурнал · без сохранения";
    public string SourceDetails => _source?.Details ?? "Демонстрационный источник";
    public string? DatabasePath => _source?.Path;
    private string BatchStorageDescription => _source?.IsLocalCopy == true ? "Изменения сохраняются в локальной копии" :
        _source?.Path is not null ? "Изменения сохраняются в MDB" : "Деморежим · без сохранения";
    private string StorageSuffix => _source?.IsLocalCopy == true ? "сохранено в локальной копии" :
        _source?.Path is not null ? "сохранено в MDB" : "до закрытия программы";
    public string? NewOrderHint => orderCreator is null && _source?.Path is not null
        ? "Изменение MDB доступно в Windows" : null;
    public bool CanConfigureSource => sourceManager is not null && CanChoosePeriod && !IsLoadingOrders && !IsRefreshingOrders && !IsCopyingResult;

    public void InitializeSource(OrderSource source)
    {
        _source = source;
        UpdateTrashBadge([]);
        NotifySourceChanged();
    }

    public DatabaseSettingsViewModel? CreateDatabaseSettingsForm() => sourceManager is not null
        ? new(DatabasePath, ConnectSourceAsync) { CurrentSource = SourceDescription } : null;

    private async Task<string?> ConnectSourceAsync(string? path, CancellationToken cancellationToken)
    {
        if (!CanConfigureSource) return "Дождитесь завершения текущего действия.";
        IsLoadingOrders = true;
        try
        {
            var candidate = sourceManager!.Create(path);
            var changed = _source?.Path != candidate.Path || _source?.Error is not null;
            var date = RequiresReload && !changed ? null : RequestedDate;
            var orders = date is { } day ? await candidate.Reader.ReadDayAsync(day, cancellationToken)
                : await candidate.Reader.ReadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // До успешной проверки и сохранения пути прежний источник и экран остаются действующими.
            sourceManager.Save(candidate);
            CancelOrderLoading();
            orderReader = candidate.Reader;
            orderCreator = candidate.Creator;
            orderEditor = candidate.Editor;
            _source = candidate;
            UpdateTrashBadge([]);
            _allOrders = orders;
            _ordersLoaded = true; _loadedDate = date; _scopeLoadFailed = false;
            RequiresReload = false;
            if (changed)
            {
                _pendingCreationForm = null;
                _pendingEdit = null;
                ResultText = "";
                _resultOrderIds.Clear();
                _resettingSearch = true;
                try { ResetSearch(resetScope: true); FilterDate = date?.ToDateTime(TimeOnly.MinValue); }
                finally { _resettingSearch = false; }
                ApplySearch();
            }
            else ApplySearch();
            NotifySourceChanged();
            ErrorMessage = (candidate.Reader as Jornal.Infrastructure.Access.OrderDurationPreparingReader)?.PreparationWarning;
            UpdatedText = $"Обновлено в {DateTime.Now:HH:mm} · F5 — обновить";
            await CleanupTrashAsync();
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OrderReadException) { throw; }
        catch (Exception exception)
        {
            AppDiagnostics.Capture(DiagnosticOperation.OrderRead, exception);
            return exception is OrderReadException ? exception.Message :
                "Не удалось подключить базу или сохранить путь. Прежний источник и список сохранены — попробуйте ещё раз.";
        }
        finally { IsLoadingOrders = false; }
    }

    private void NotifySourceChanged()
    {
        foreach (var name in new[] { nameof(SourceDescription), nameof(SourceDetails), nameof(DatabasePath), nameof(NewOrderHint) }) OnPropertyChanged(name);
        NotifyJournalAvailability();
    }
}
