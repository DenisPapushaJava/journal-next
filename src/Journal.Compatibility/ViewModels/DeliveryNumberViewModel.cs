using Jornal.Infrastructure.Diagnostics;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;

namespace Jornal.Desktop.ViewModels;

public partial class DeliveryNumberViewModel : ObservableObject
{
    private readonly Func<string, Task<bool>> _save;
    private readonly string _initial;
    private bool _clearRequested;
    private bool _submitted;
    private readonly Func<string?>? _saveError;
    private readonly Func<bool>? _canRetry;

    public DeliveryNumberViewModel(IReadOnlyList<OrderSummary> orders, Func<string, Task<bool>> save,
        string storageDescription = "Деморежим · до закрытия программы", Func<string?>? saveError = null, Func<bool>? canRetry = null,
        AccessHostHelpViewModel? accessHostHelp = null)
    {
        if (orders.Count == 0) throw new ArgumentException("Не выбраны заявки.", nameof(orders));
        _save = save;
        _saveError = saveError;
        _canRetry = canRetry;
        AccessHostHelp = accessHostHelp ?? new();
        StorageDescription = storageDescription;
        var values = orders.Select(order => order.DeliveryNumber ?? "").Distinct().ToArray();
        HasDifferentNumbers = values.Length > 1;
        _initial = values.Length == 1 ? values[0] : "";
        _number = _initial;
        SelectionDescription = $"Выбрано заявок: {orders.Count}";
        Specifications = orders.Select(order => $"{order.SpecificationNumber} · {order.CustomerName}").ToArray();
    }

    public bool HasDifferentNumbers { get; }
    public AccessHostHelpViewModel AccessHostHelp { get; }
    public string StorageDescription { get; }
    public string SelectionDescription { get; }
    public IReadOnlyList<string> Specifications { get; }
    [ObservableProperty] private string _number;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand))] private bool _isSaved;
    public bool HasError => Error is not null;
    public event EventHandler? Saved;

    partial void OnNumberChanged(string value)
    {
        if (!CanRetry) Error = _saveError?.Invoke() ?? Error;
        else if (_submitted) Validate();
        else Error = null;
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearNumber()
    {
        if (SaveCommand.IsRunning) return;
        _clearRequested = true;
        Number = "";
        if (CanRetry) Error = null;
        SaveCommand.NotifyCanExecuteChanged();
    }

    private bool CanRetry => _canRetry?.Invoke() != false;
    private bool CanSave() => !IsSaved && CanRetry &&
        (HasDifferentNumbers ? Number.Length > 0 || _clearRequested : Number != _initial);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (SaveCommand.IsRunning || !CanSave()) return;
        _submitted = true;
        if (!Validate()) return;
        try
        {
            if (!await _save(Number))
            {
                Error = _saveError?.Invoke() ?? "Не удалось сохранить БЛЦБ. Номер оставлен в поле — попробуйте ещё раз.";
                SaveCommand.NotifyCanExecuteChanged();
                return;
            }
        }
        catch (Exception exception)
        {
            AppDiagnostics.Capture(DiagnosticOperation.OrderBatch, exception);
            Error = "Не удалось сохранить БЛЦБ. Номер оставлен в поле — попробуйте ещё раз.";
            Error = AppDiagnostics.WithCode(Error, AppDiagnostics.Capture(DiagnosticOperation.OrderBatch, exception));
            return;
        }
        IsSaved = true;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private bool Validate()
    {
        Error = Number.Length > 255 ? "Номер не должен превышать 255 символов." :
            Number.Any(char.IsControl) ? "Номер должен занимать одну строку." :
            Number.Length > 0 && string.IsNullOrWhiteSpace(Number) ? "Введите номер или нажмите «Очистить номер»." : null;
        return !HasError;
    }
}
