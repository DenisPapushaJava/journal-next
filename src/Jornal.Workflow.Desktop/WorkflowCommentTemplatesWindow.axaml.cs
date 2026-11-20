using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowCommentTemplatesWindow : Window
{
    private WorkflowViewModel? _journal;
    private string _expected = "";
    private bool _saving;

    public WorkflowCommentTemplatesWindow() => InitializeComponent();

    internal WorkflowCommentTemplatesWindow(WorkflowViewModel journal) : this()
    {
        _journal = journal;
        _expected = journal.CommentTemplatesText;
        CommentTemplatesInput.Text = _expected;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        CommentTemplatesInput.Focus();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_saving) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_saving || _journal is not { } journal) return;
        _saving = true;
        CommentTemplatesInput.IsEnabled = SaveCommentTemplates.IsEnabled = CancelCommentTemplates.IsEnabled = false;
        var error = await journal.SaveCommentTemplatesAsync(_expected, CommentTemplatesInput.Text ?? "");
        _saving = false;
        if (error is null) { Close(); return; }

        CommentTemplatesInput.IsEnabled = SaveCommentTemplates.IsEnabled = CancelCommentTemplates.IsEnabled = true;
        CommentTemplatesError.Text = error;
        CommentTemplatesError.IsVisible = true;
    }
}
