using System.Collections.ObjectModel;
using Jornal.Infrastructure.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;
using Jornal.Infrastructure.Trash;

namespace Jornal.Desktop.ViewModels;

public sealed record TrashItem(TrashEntry Entry)
{
    public string Specification => Entry.Record.Specification ?? "—";
    public string Customer => Entry.Record.Customer ?? "—";
    public string DeletedAt => Entry.DeletedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
    public string State => Entry.State switch { TrashState.Deleted => "В корзине", TrashState.DeletePending => "Проверить удаление", _ => "Проверить восстановление" };
    public string Retention => Entry.State == TrashState.Deleted
        ? $"Осталось дней: {Math.Max(0, (int)Math.Ceiling(Entry.RetentionPeriodDays - (DateTimeOffset.UtcNow - (Entry.ConfirmedAt ?? Entry.DeletedAt)).TotalDays))}"
        : "Хранится до проверки";
}

public partial class TrashViewModel(OrderTrashService service, string sourceDescription, Action<int, OrderRecord?> changed,
    Func<CancellationToken, Task> ensureReload, Func<Func<Task>, Task> runWrite, Action<Exception> failed,
    Action<IReadOnlyList<TrashEntry>>? entriesChanged = null) : ObservableObject
{
    public string SourceDescription { get; } = sourceDescription;
    public ObservableCollection<TrashItem> Items { get; } = [];
    public AccessHostHelpViewModel AccessHostHelp { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanAct)), NotifyPropertyChangedFor(nameof(SelectedDetails))] private TrashItem? _selected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanAct)), NotifyPropertyChangedFor(nameof(CanClear))] private bool _isWriting;
    public bool HasError => Error is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy)), NotifyPropertyChangedFor(nameof(CanAct)), NotifyPropertyChangedFor(nameof(CanClear))] private bool _isRefreshing;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanAct)), NotifyPropertyChangedFor(nameof(CanClear))] private bool _needsRefresh;
    public bool IsBusy => IsWriting || IsRefreshing;
    public bool CanAct => Selected?.Entry.State == TrashState.Deleted && !IsBusy && !NeedsRefresh;
    public bool CanClear => Items.Any(item => item.Entry.State == TrashState.Deleted) && !IsBusy && !NeedsRefresh;
    public string CountText => $"Записей в корзине: {Items.Count}";
    public string SelectedDetails => Selected is { Entry.Record: var row } ?
        MainWindowViewModel.Describe(row) + $"\nДата исходная: {row.Date:dd.MM.yyyy HH:mm:ss} · {row.CuttingType} · {row.Material} · {row.Thickness} мм\n" +
        $"Лист: {row.SheetSize} · количество: {row.SheetCount} · вес: {row.Weight} · рез: {row.CutLength} м\n" +
        $"Цена: {row.Price?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "—"} · БЛЦБ: {row.DeliveryNumber ?? "—"} · цвет: {row.Color?.ToString() ?? "—"}\nКоэффициент: {row.Coefficient ?? "—"}" : "";

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (IsWriting) return;
        IsRefreshing = true; Error = null;
        try
        {
            await ensureReload(cancellationToken);
            var entries = await service.RefreshAsync(reconcile: true, cancellationToken);
            SetEntries(entries); NeedsRefresh = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { NeedsRefresh = true; Error = AppDiagnostics.Describe(DiagnosticOperation.Trash, exception, "Не удалось прочитать корзину."); AccessHostHelp.ShowFor(exception); }
        finally { IsRefreshing = false; }
    }

    partial void OnIsWritingChanged(bool value) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanAct)); }

    private void SetEntries(IReadOnlyList<TrashEntry> entries, bool notifyBadge = true)
    {
        var selected = Selected?.Entry.OperationId;
        Items.Clear(); foreach (var entry in entries) Items.Add(new(entry));
        Selected = Items.FirstOrDefault(item => item.Entry.OperationId == selected) ?? Items.FirstOrDefault();
        OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(CanClear));
        if (notifyBadge) entriesChanged?.Invoke(entries);
    }

    public OrderConfirmationViewModel? CreateRestoreConfirmation() => CanAct && Selected is { } item ? Confirmation(
        "Восстановить заявку?", MainWindowViewModel.Describe(item.Entry.Record),
        "Вернутся исходные данные и цена.\nИсточник: " + SourceDescription,
        "Восстановить", async () =>
        {
            var restored = await service.RestoreAsync(item.Entry.OperationId);
            changed(restored.Id, restored);
        }) : null;

    public OrderConfirmationViewModel? CreatePurgeConfirmation() => CanAct && Selected is { } item ? Confirmation(
        "Удалить копию окончательно?", MainWindowViewModel.Describe(item.Entry.Record),
        "Удалится только архивная копия. Восстановление станет недоступно.",
        "Удалить окончательно", () => service.PurgeAsync(item.Entry.OperationId), destructive: true) : null;

    public OrderConfirmationViewModel? CreateClearConfirmation()
    {
        if (!CanClear) return null;
        var snapshot = Items.Where(item => item.Entry.State == TrashState.Deleted).Select(item => item.Entry).ToArray();
        var pending = Items.Count - snapshot.Length;
        OrderConfirmationViewModel form = null!;
        form = Confirmation("Очистить корзину?", $"Архивных копий: {snapshot.Length}",
            "Копии будут удалены окончательно. Восстановление станет недоступно.\nИсточник: " + SourceDescription +
            (pending > 0 ? $"\nТребуют проверки: {pending} — останутся в корзине." : ""), "Очистить корзину", async () =>
        {
            TrashPurgeResult result;
            try { result = await service.PurgeManyAsync(snapshot); }
            catch (Exception exception)
            {
                NeedsRefresh = true;
                form.ShowOutcome("Очистка не выполнена.\n" + PurgeError(exception) + "\nОбновите корзину перед следующим действием.", exception);
                return;
            }
            if (result.Remaining is { } entries) SetEntries(entries);
            else
            {
                var removed = result.RemovedIds.ToHashSet();
                SetEntries(Items.Where(item => !removed.Contains(item.Entry.OperationId)).Select(item => item.Entry).ToArray(), notifyBadge: false);
            }
            if (result.Error is { } error)
            {
                NeedsRefresh = true;
                form.ShowOutcome($"Удалено копий: {result.RemovedIds.Count} из {snapshot.Length}. Очистка остановлена.\n" +
                    PurgeError(error) + "\nОбновите корзину перед следующим действием.", error);
            }
        }, destructive: true, archiveOnly: true);
        return form;
    }

    private static string PurgeError(Exception exception) => AppDiagnostics.Describe(DiagnosticOperation.Trash, exception,
        "Не удалось очистить локальную корзину. Проверьте доступ к её папке.");

    private OrderConfirmationViewModel Confirmation(string title, string details, string explanation, string label, Func<Task> action,
        bool destructive = false, bool archiveOnly = false) =>
        new(title, details, explanation, label, async () =>
        {
            if (IsBusy) throw new InvalidOperationException("Дождитесь завершения действия.");
            IsWriting = true;
            try { await runWrite(action); }
            finally { IsWriting = false; }
        }, exception =>
        {
            Error = AppDiagnostics.Describe(DiagnosticOperation.Trash, exception, "Не удалось выполнить действие.");
            if (!archiveOnly) failed(exception);
        }, destructive);
}
