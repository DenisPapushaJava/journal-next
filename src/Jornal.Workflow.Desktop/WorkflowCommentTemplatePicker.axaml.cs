using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Jornal.Workflow.Desktop;

internal sealed record WorkflowCommentTemplateChanges(string[] Added, string[] Removed);

public partial class WorkflowCommentTemplateChoice(string text, bool alreadyInserted) : ObservableObject
{
    public string Text { get; } = text;
    public bool WasInserted { get; } = alreadyInserted;
    public bool CanSelect => !CanRemove;
    public bool CanRemove => WasInserted && Selected;
    public bool HasChanges => WasInserted != Selected;
    public string? Status => CanRemove ? "Уже в комментарии" : WasInserted ? "Будет убран после применения" : null;

    [NotifyPropertyChangedFor(nameof(CanSelect), nameof(CanRemove), nameof(HasChanges), nameof(Status))]
    [ObservableProperty] private bool _selected = alreadyInserted;
}

public partial class WorkflowCommentTemplatePicker : Window
{
    private readonly WorkflowCommentTemplateChoice[] _choices = [];

    public WorkflowCommentTemplatePicker() => InitializeComponent();

    internal WorkflowCommentTemplatePicker(string[] templates, string? comment) : this()
    {
        var existing = WorkflowViewModel.CommentTemplateLines(comment ?? "").ToHashSet(StringComparer.Ordinal);
        _choices = templates.Select(text => new WorkflowCommentTemplateChoice(text, existing.Contains(text))).ToArray();
        foreach (var choice in _choices)
            choice.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(WorkflowCommentTemplateChoice.HasChanges))
                    ApplyCommentTemplates.IsEnabled = _choices.Any(item => item.HasChanges);
            };
        CommentTemplateChoices.ItemsSource = _choices;
        if (_choices.Length == 0)
            CommentTemplatePickerHint.Text = "Добавьте фразы в «Настройки → Общие → Шаблоны комментариев».";
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (CommentTemplateChoices.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.IsEffectivelyVisible && button.IsEnabled) is { } first)
            first.Focus();
        else
            CloseCommentTemplatePicker.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WorkflowCommentTemplateChoice { CanRemove: true } choice } button) return;
        choice.Selected = false;
        button.FindAncestorOfType<Grid>()?.Children.OfType<CheckBox>().FirstOrDefault()?.Focus();
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        var added = _choices.Where(choice => choice.Selected && !choice.WasInserted).Select(choice => choice.Text).ToArray();
        var removed = _choices.Where(choice => !choice.Selected && choice.WasInserted).Select(choice => choice.Text).ToArray();
        if (added.Length > 0 || removed.Length > 0) Close(new WorkflowCommentTemplateChanges(added, removed));
    }
}
