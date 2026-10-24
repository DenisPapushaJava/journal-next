using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Results;
using Jornal.Desktop.Clipboard;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public partial class FolderClipboardViewModel(OrderFolderPlan plan, IJournalClipboard clipboard) : ObservableObject
{
    public IReadOnlyList<OrderFolderItem> Items => plan.Items;
    public string Summary => $"Папки: {Items.Count}";
    public string RenamedSummary => $"Изменены имена: {Items.Count(item => item.Changed)}";
    public bool HasRenamed => Items.Any(item => item.Changed);
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanCopy))] private bool _isBusy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string? _error;
    public bool CanCopy => !IsBusy;
    public bool HasError => Error is not null;
    public event EventHandler? BusyChanged;
    public event EventHandler? Copied;
    partial void OnIsBusyChanged(bool value) { CopyCommand.NotifyCanExecuteChanged(); BusyChanged?.Invoke(this, EventArgs.Empty); }
    [RelayCommand(CanExecute = nameof(CanCopy))]
    private async Task CopyAsync()
    {
        Error = null; IsBusy = true; var copied = false;
        try { await clipboard.WriteFoldersAsync(plan); copied = true; }
        catch (Exception exception) { Error = AppDiagnostics.Describe(DiagnosticOperation.Clipboard, exception, "Не удалось подготовить папки для вставки."); }
        finally { IsBusy = false; }
        if (copied) Copied?.Invoke(this, EventArgs.Empty);
    }
}
