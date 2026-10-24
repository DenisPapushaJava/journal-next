using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jornal.Core.Orders;
using Jornal.Core.Results;
using Jornal.Desktop.Clipboard;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private enum ClipboardPreparation { Row, CuttingTask, Folders }
    private sealed record SelectionNotice(ClipboardPreparation Action, HashSet<int> Ids, string Message, int FolderNameLimit);
    private SelectionNotice? _selectionNotice;
    public bool HasSelectionNotice => _selectionNotice is not null;
    private void ShowSelectionNotice(ClipboardPreparation action, ArgumentException exception, int folderNameLimit = OrderFolderPlan.MaximumNameLength)
    {
        _selectionNotice = new(action, _selectedOrderIds.ToHashSet(), exception.Message, folderNameLimit);
        ErrorMessage = exception.Message;
        OnPropertyChanged(nameof(HasSelectionNotice));
    }
    [RelayCommand]
    private void DismissSelectionNotice()
    {
        if (_selectionNotice is null) return;
        _selectionNotice = null;
        ErrorMessage = null;
        OnPropertyChanged(nameof(HasSelectionNotice));
    }
    private void RefreshSelectionNotice()
    {
        if (IsUpdatingOrders) return; // Восстановление выделения таблицы проходит через промежуточный пустой набор.
        if (_selectionNotice is not { } notice) return;
        if (!notice.Ids.SetEquals(_selectedOrderIds)) { DismissSelectionNotice(); return; }
        try
        {
            var rows = ClipboardSelection();
            switch (notice.Action)
            {
                case ClipboardPreparation.Row: OrderCommentFormatter.Row(rows.Single()); break;
                case ClipboardPreparation.CuttingTask: OrderCommentFormatter.Telegram(rows); break;
                case ClipboardPreparation.Folders: OrderFolderPlan.Create(rows, notice.FolderNameLimit); break;
            }
            DismissSelectionNotice();
        }
        catch (ArgumentException exception) { ShowSelectionNotice(notice.Action, exception, notice.FolderNameLimit); }
    }
    [ObservableProperty] private bool _isCopyingSelection;
    public bool CanCopyRowComment => CanUseJournal && !RequiresReload && !IsCopyingResult && _selectedCount == 1;
    public bool CanCopyTelegramComment => CanUseJournal && !RequiresReload && !IsCopyingResult && _selectedCount > 0;
    public bool CanPrepareFolders => CanCopyTelegramComment;
    public string RowCommentHint => _selectedCount == 1 ? "Скопировать комментарий строки" : "Выберите одну заявку";
    private OrderSummary[] ClipboardSelection() => Orders.Where(row => _selectedOrderIds.Contains(row.Id)).Select(row => row.Source).ToArray();
    private void NotifyClipboardAvailability()
    {
        foreach (var name in new[] { nameof(CanCopyRowComment), nameof(CanCopyTelegramComment), nameof(CanPrepareFolders), nameof(RowCommentHint) }) OnPropertyChanged(name);
    }
    partial void OnIsCopyingSelectionChanged(bool value) { NotifyJournalAvailability(); NotifyClipboardAvailability(); }
    partial void OnIsCopyingResultChanged(bool value) => NotifyClipboardAvailability();
    public async Task CopyCommentAsync(IJournalClipboard clipboard, bool telegram = false)
    {
        if (!(telegram ? CanCopyTelegramComment : CanCopyRowComment)) return;
        string text;
        try { var rows = ClipboardSelection(); text = telegram ? OrderCommentFormatter.Telegram(rows) : OrderCommentFormatter.Row(rows.Single()); }
        catch (ArgumentException exception) { ShowSelectionNotice(telegram ? ClipboardPreparation.CuttingTask : ClipboardPreparation.Row, exception); return; }
        ErrorMessage = null; IsCopyingSelection = true; AppDiagnostics.Current?.Mark(DiagnosticOperation.Clipboard);
        try { await clipboard.WriteTextAsync(text); UpdatedText = telegram ? "Задание на резку скопировано" : "Комментарий скопирован"; }
        catch (Exception exception) { ErrorMessage = AppDiagnostics.Describe(DiagnosticOperation.Clipboard, exception, "Не удалось скопировать комментарий."); }
        finally { IsCopyingSelection = false; }
    }
    public FolderClipboardViewModel? CreateFolderForm(IJournalClipboard clipboard)
    {
        if (!CanPrepareFolders) return null;
        try
        {
            var form = new FolderClipboardViewModel(OrderFolderPlan.Create(ClipboardSelection(), clipboard.MaximumFolderNameLength), clipboard);
            form.BusyChanged += (_, _) => IsCopyingSelection = form.IsBusy;
            form.Copied += (_, _) => UpdatedText = $"Папки готовы к вставке: {form.Items.Count}";
            ErrorMessage = null; return form;
        }
        catch (ArgumentException exception) { ShowSelectionNotice(ClipboardPreparation.Folders, exception, clipboard.MaximumFolderNameLength); return null; }
    }
}
