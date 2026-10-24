using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jornal.Desktop.Controls;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Views;

public partial class FolderClipboardWindow : Window
{
    private FolderClipboardViewModel? Model => DataContext as FolderClipboardViewModel;
    public FolderClipboardWindow() => InitializeComponent();
    protected override void OnOpened(EventArgs e) { base.OnOpened(e); WindowSizing.FitToWorkingArea(this); if (Model is { } model) model.Copied += OnCopied; }
    protected override void OnClosing(WindowClosingEventArgs e) { if (Model?.IsBusy == true) e.Cancel = true; base.OnClosing(e); }
    protected override void OnClosed(EventArgs e) { if (Model is { } model) model.Copied -= OnCopied; base.OnClosed(e); }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.Key == Key.Escape && Model?.IsBusy != true) { Close(); e.Handled = true; } }
    private void OnCopied(object? sender, EventArgs e) => Close();
    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
