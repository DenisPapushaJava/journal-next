using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public partial class OrderConfirmationViewModel(string title, string details, string explanation, string confirmLabel,
    Func<Task> confirm, Action<Exception>? failed = null, bool destructive = false) : ObservableObject
{
    public bool IsDestructive { get; } = destructive;
    public string Title { get; } = title;
    public string Details { get; } = details;
    public string Explanation { get; } = explanation;
    public string ConfirmLabel { get; } = confirmLabel;
    public AccessHostHelpViewModel AccessHostHelp { get; } = new();
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConfirm))] private bool _isConfirmed;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConfirm))] private bool _requiresReload;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanConfirm)), NotifyPropertyChangedFor(nameof(HasOutcome)), NotifyPropertyChangedFor(nameof(CancelLabel))]
    private string? _outcome;
    [ObservableProperty] private string? _progress;
    public bool HasOutcome => Outcome is not null;
    public string CancelLabel => HasOutcome ? "Закрыть" : "Отмена";
    public bool CanConfirm => !IsConfirmed && !RequiresReload && !HasOutcome;
    public bool HasError => Error is not null;
    public event EventHandler? Confirmed;
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (!CanConfirm) return;
        Error = null;
        try
        {
            await confirm();
            if (!HasOutcome) { IsConfirmed = true; Confirmed?.Invoke(this, EventArgs.Empty); }
        }
        catch (Exception exception)
        {
            Error = AppDiagnostics.Describe(DiagnosticOperation.Trash, exception, "Не удалось выполнить действие."); AccessHostHelp.ShowFor(exception);
            RequiresReload = exception is Jornal.Core.Orders.OrderWriteException { RequiresReload: true };
            failed?.Invoke(exception);
        }
    }

    public void ShowOutcome(string message, Exception? exception)
    {
        Progress = null;
        Outcome = message;
        if (exception is null) return;
        RequiresReload = exception is Jornal.Core.Orders.OrderWriteException { RequiresReload: true };
        AccessHostHelp.ShowFor(exception);
        failed?.Invoke(exception);
    }
}
