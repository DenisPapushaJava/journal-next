using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowTrashConfirmationViewModel(WorkflowOrder order, string customer,
    Func<CancellationToken, Task<string?>> confirm) : ObservableObject
{
    public string Title => "В корзину: " + order.DisplayNumber + "?";
    public string Customer => customer;
    public string Contents => $"Проектов: {order.Projects.Length} · спецификаций: {order.Specifications.Length}. Расчёты сохранятся.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    public bool CanEdit => !IsBusy;
    public bool WasMoved { get; private set; }
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); ConfirmCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanEdit))] private async Task ConfirmAsync(CancellationToken token)
    {
        IsBusy = true; Error = null;
        try { Error = await confirm(token); WasMoved = Error is null; }
        catch (OperationCanceledException) { Error = "Перемещение отменено."; }
        finally { IsBusy = false; }
    }
}

public sealed record WorkflowTrashRow(WorkflowOrder Order, string Customer, string DeletedBy)
{
    public string Number => Order.DisplayNumber;
    public string DeletedAt => Order.DeletedAt?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "";
    public string Details => $"Проектов: {Order.Projects.Length} · спецификаций: {Order.Specifications.Length}";
}

public partial class WorkflowTrashViewModel : ObservableObject
{
    private readonly Func<WorkflowWorkspace> _snapshot;
    private readonly Func<CancellationToken, Task<WorkflowWorkspace>> _refresh;
    private readonly Func<Guid, long, CancellationToken, Task<string?>> _restore;
    private readonly Func<IReadOnlyList<WorkflowTrashTarget>, CancellationToken, Task<string?>> _purge;
    private readonly Func<CancellationToken, Task<WorkflowWorkspace>>? _more;
    private readonly Func<bool> _hasMore;
    private readonly Func<int>? _total;
    public bool HasMore => _hasMore();
    public ObservableCollection<WorkflowTrashRow> Orders { get; } = [];
    [ObservableProperty] private WorkflowTrashRow? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _requiresRefresh;
    public bool CanEdit => !IsBusy;
    public bool IsEmpty => Orders.Count == 0;
    public string Summary => $"В корзине: {(_total is null ? Orders.Count.ToString() : $"{Orders.Count} из {_total()}")}. Можно восстановить или удалить навсегда. Автоочистки нет." +
        (HasMore ? " Для очистки всей корзины загрузите остальные заявки." : "");
    private bool CanRestore => CanEdit && !RequiresRefresh && Selected is { } selected &&
        Orders.Any(row => row.Order.Id == selected.Order.Id && row.Order.Version == selected.Order.Version);
    public bool CanPurgeSelected => CanRestore;
    public bool CanClearTrash => CanEdit && !RequiresRefresh && !HasMore && Orders.Count > 0;
    public WorkflowTrashViewModel(Func<WorkflowWorkspace> snapshot, Func<CancellationToken, Task<WorkflowWorkspace>> refresh,
        Func<Guid, long, CancellationToken, Task<string?>> restore,
        Func<IReadOnlyList<WorkflowTrashTarget>, CancellationToken, Task<string?>> purge,
        Func<CancellationToken, Task<WorkflowWorkspace>>? more = null, Func<bool>? hasMore = null, Func<int>? total = null)
    { _snapshot = snapshot; _refresh = refresh; _restore = restore; _purge = purge; _more = more; _hasMore = hasMore ?? (() => false); _total = total; Populate(snapshot()); }
    private void Populate(WorkflowWorkspace book)
    {
        var selected = Selected?.Order.Id; Orders.Clear();
        foreach (var order in book.Orders.Where(o => o.IsDeleted).OrderByDescending(o => o.DeletedAt).ThenByDescending(o => o.Number))
            Orders.Add(new(order, book.Customers.Single(c => c.Id == order.CustomerId).Name,
                book.Employees.Single(e => e.Id == order.DeletedBy).Name));
        Selected = Orders.SingleOrDefault(o => o.Order.Id == selected) ?? Orders.FirstOrDefault();
        OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasMore));
        ActionsChanged();
    }
    private void ActionsChanged()
    {
        OnPropertyChanged(nameof(CanPurgeSelected)); OnPropertyChanged(nameof(CanClearTrash)); RestoreCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedChanged(WorkflowTrashRow? value) => ActionsChanged();
    partial void OnRequiresRefreshChanged(bool value) => ActionsChanged();
    partial void OnIsBusyChanged(bool value)
    { OnPropertyChanged(nameof(CanEdit)); ReloadCommand.NotifyCanExecuteChanged(); ActionsChanged(); }
    [RelayCommand(CanExecute = nameof(CanEdit))] private async Task ReloadAsync(CancellationToken token)
    {
        IsBusy = true; Error = null;
        try { Populate(await _refresh(token)); RequiresRefresh = false; Status = null; }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Error = ex is WorkflowValidationException ? ex.Message : "Не удалось прочитать корзину. Повторите обновление."; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanEdit))] private async Task LoadMoreAsync(CancellationToken token)
    {
        if (_more is null || !HasMore || RequiresRefresh) return;
        IsBusy = true; Error = null;
        try { Populate(await _more(token)); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Error = "Не удалось прочитать следующую страницу корзины. Обновите корзину."; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanRestore))] private async Task RestoreAsync(CancellationToken token)
    {
        if (!CanRestore || Selected is not { } selected) return;
        IsBusy = true; Error = null; Status = null;
        try
        {
            Error = await _restore(selected.Order.Id, selected.Order.Version, token);
            if (Error is null) { Populate(_snapshot()); Status = "Заявка " + selected.Number + " восстановлена."; }
        }
        catch (OperationCanceledException) { }
        finally { IsBusy = false; }
    }

    public WorkflowTrashPurgeConfirmationViewModel? CreatePurgeConfirmation(bool all)
    {
        if (all ? !CanClearTrash : !CanPurgeSelected) return null;
        var rows = all ? Orders.ToArray() : new[] { Selected! };
        var targets = rows.Select(row => new WorkflowTrashTarget(row.Order.Id, row.Order.Version)).ToArray();
        return new(rows, all, token => PurgeAsync(targets, token));
    }

    private async Task<string?> PurgeAsync(IReadOnlyList<WorkflowTrashTarget> targets, CancellationToken token)
    {
        if (!CanEdit || RequiresRefresh) return "Обновите корзину перед удалением.";
        IsBusy = true; Error = null; Status = null;
        try
        {
            Error = await _purge(targets, token);
            if (Error is null)
            {
                Populate(_snapshot()); Status = $"Удалено заявок: {targets.Count}. Папки и файлы сохранены.";
            }
        }
        catch (OperationCanceledException) { Error = "Удаление не подтверждено. Обновите корзину."; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Error = "Не удалось подтвердить удаление. Обновите корзину перед повторной операцией."; }
        finally
        {
            if (Error is not null)
            {
                RequiresRefresh = true;
                Error += " Для продолжения нажмите «Обновить».";
            }
            IsBusy = false;
        }
        return Error;
    }
}

public partial class WorkflowTrashPurgeConfirmationViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<string?>> _confirm;
    public string Title { get; }
    public string Summary { get; }
    public string Details { get; }
    public string ConfirmLabel { get; }
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _requiresRefresh;
    public bool WasPurged { get; private set; }
    public bool CanClose => !IsBusy;
    public bool CanConfirm => !IsBusy && !RequiresRefresh && !WasPurged;
    public WorkflowTrashPurgeConfirmationViewModel(WorkflowTrashRow[] rows, bool all, Func<CancellationToken, Task<string?>> confirm)
    {
        _confirm = confirm;
        Title = all ? "Очистить корзину?" : "Удалить заявку навсегда?";
        Summary = $"Заявок: {rows.Length} · проектов: {rows.Sum(row => row.Order.Projects.Length)} · спецификаций: {rows.Sum(row => row.Order.Specifications.Length)}";
        Details = string.Join("\n", rows.Take(5).Select(row => row.Number + " · " + row.Customer)) +
            (rows.Length > 5 ? $"\n…и ещё {rows.Length - 5}" : "");
        ConfirmLabel = all ? "Удалить все навсегда" : "Удалить навсегда";
    }
    private void ActionsChanged()
    { OnPropertyChanged(nameof(CanClose)); OnPropertyChanged(nameof(CanConfirm)); ConfirmCommand.NotifyCanExecuteChanged(); }
    partial void OnIsBusyChanged(bool value) => ActionsChanged();
    partial void OnRequiresRefreshChanged(bool value) => ActionsChanged();
    [RelayCommand(CanExecute = nameof(CanConfirm))] private async Task ConfirmAsync(CancellationToken token)
    {
        if (!CanConfirm) return;
        IsBusy = true; Error = null;
        try
        {
            Error = await _confirm(token); WasPurged = Error is null;
            if (!WasPurged) RequiresRefresh = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Error = "Удаление не подтверждено. Закройте это окно и обновите корзину."; RequiresRefresh = true; }
        finally { IsBusy = false; ActionsChanged(); }
    }
}
