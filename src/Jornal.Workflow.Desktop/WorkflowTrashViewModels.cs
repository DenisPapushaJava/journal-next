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
    public string Contents => $"Проектов: {order.Projects.Length} · спецификаций: {order.Specifications.Length}. Расчёты и история сохранятся.";
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
    public ObservableCollection<WorkflowTrashRow> Orders { get; } = [];
    [ObservableProperty] private WorkflowTrashRow? _selected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _status;
    public bool CanEdit => !IsBusy;
    public bool IsEmpty => Orders.Count == 0;
    public string Summary => $"В корзине: {Orders.Count}. Заявки хранятся до восстановления; автоочистки нет.";
    private bool CanRestore => CanEdit && Selected is not null;
    public WorkflowTrashViewModel(Func<WorkflowWorkspace> snapshot, Func<CancellationToken, Task<WorkflowWorkspace>> refresh,
        Func<Guid, long, CancellationToken, Task<string?>> restore)
    { _snapshot = snapshot; _refresh = refresh; _restore = restore; Populate(snapshot()); }
    private void Populate(WorkflowWorkspace book)
    {
        var selected = Selected?.Order.Id; Orders.Clear();
        foreach (var order in book.Orders.Where(o => o.IsDeleted).OrderByDescending(o => o.DeletedAt).ThenByDescending(o => o.Number))
            Orders.Add(new(order, book.Customers.Single(c => c.Id == order.CustomerId).Name,
                book.Employees.Single(e => e.Id == order.DeletedBy).Name));
        Selected = Orders.SingleOrDefault(o => o.Order.Id == selected) ?? Orders.FirstOrDefault();
        OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(IsEmpty));
    }
    partial void OnSelectedChanged(WorkflowTrashRow? value) => RestoreCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value)
    { OnPropertyChanged(nameof(CanEdit)); ReloadCommand.NotifyCanExecuteChanged(); RestoreCommand.NotifyCanExecuteChanged(); }
    [RelayCommand(CanExecute = nameof(CanEdit))] private async Task ReloadAsync(CancellationToken token)
    {
        IsBusy = true; Error = null;
        try { Populate(await _refresh(token)); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { Error = ex is WorkflowValidationException ? ex.Message : "Не удалось прочитать корзину. Повторите обновление."; }
        finally { IsBusy = false; }
    }
    [RelayCommand(CanExecute = nameof(CanRestore))] private async Task RestoreAsync(CancellationToken token)
    {
        if (Selected is not { } selected) return;
        IsBusy = true; Error = null; Status = null;
        try
        {
            Error = await _restore(selected.Order.Id, selected.Order.Version, token);
            if (Error is null) { Populate(_snapshot()); Status = "Заявка " + selected.Number + " восстановлена."; }
        }
        catch (OperationCanceledException) { }
        finally { IsBusy = false; }
    }
}
