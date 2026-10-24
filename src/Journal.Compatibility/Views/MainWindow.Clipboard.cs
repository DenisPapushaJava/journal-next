using Avalonia.Interactivity;
using Jornal.Desktop.Clipboard;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.Views;

public partial class MainWindow
{
    private JournalClipboard SelectionClipboard() => new(() => Clipboard, StorageProvider);
    private async void OnCopyRowCommentClick(object? sender, RoutedEventArgs e)
    { if (_viewModel is { } model) await model.CopyCommentAsync(SelectionClipboard()); }
    private async void OnCopyTelegramCommentClick(object? sender, RoutedEventArgs e)
    { if (_viewModel is { } model) await model.CopyCommentAsync(SelectionClipboard(), telegram: true); }
    private async void OnCreateFoldersClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } model) return;
        try
        {
            if (model.CreateFolderForm(SelectionClipboard()) is { } form)
                await new FolderClipboardWindow { DataContext = form, RequestedThemeVariant = ActualThemeVariant }.ShowDialog(this);
        }
        catch (Exception exception) { model.ErrorMessage = AppDiagnostics.Describe(DiagnosticOperation.Clipboard, exception, "Не удалось открыть создание папок."); }
    }
}
