using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Jornal.Desktop.Controls;

// Текст набора отделён от применённого запроса. В журнал попадает только последний ввод.
public sealed class JournalSearchBox : TextBox
{
    public static readonly StyledProperty<string?> QueryProperty =
        AvaloniaProperty.Register<JournalSearchBox, string?>(nameof(Query), "");
    public static readonly DirectProperty<JournalSearchBox, bool> IsPendingProperty =
        AvaloniaProperty.RegisterDirect<JournalSearchBox, bool>(nameof(IsPending), box => box.IsPending);
    public static readonly DirectProperty<JournalSearchBox, bool> HasInputProperty =
        AvaloniaProperty.RegisterDirect<JournalSearchBox, bool>(nameof(HasInput), box => box.HasInput);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private bool _isPending;
    private bool _hasInput;
    public string? Query { get => GetValue(QueryProperty); set => SetValue(QueryProperty, value); }
    public bool IsPending => _isPending;
    public bool HasInput => _hasInput;
    public event EventHandler? PendingChanged;
    public event EventHandler? ClearRequested;

    public JournalSearchBox()
    {
        _timer.Tick += (_, _) => SubmitInput();
        TextChanged += (_, _) =>
        {
            SetAndRaise(HasInputProperty, ref _hasInput, !string.IsNullOrEmpty(Text));
            _timer.Stop();
            if (string.Equals(Text ?? "", Query ?? "", StringComparison.Ordinal)) { SetPending(false); return; }
            if (string.IsNullOrWhiteSpace(Text)) { SubmitInput(); return; }
            SetPending(true);
            _timer.Start();
        };
    }

    protected override Type StyleKeyOverride => typeof(TextBox);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == QueryProperty) CancelPendingInput();
    }
    public void SubmitInput()
    {
        _timer.Stop();
        SetPending(false);
        SetCurrentValue(QueryProperty, Text ?? "");
    }
    public void CancelPendingInput()
    {
        _timer.Stop();
        SetPending(false);
        SetCurrentValue(TextProperty, Query ?? "");
    }
    public void ClearInput()
    {
        if (ClearRequested is { } clear) clear(this, EventArgs.Empty);
        else SetCurrentValue(QueryProperty, "");
        CancelPendingInput();
    }
    private void SetPending(bool value)
    {
        if (SetAndRaise(IsPendingProperty, ref _isPending, value)) PendingChanged?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key == Key.Enter) { SubmitInput(); e.Handled = true; return; }
            if (e.Key == Key.Escape) { ClearInput(); e.Handled = true; return; }
            if (e.Key == Key.F5) SubmitInput();
        }
        base.OnKeyDown(e);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingInput();
        base.OnDetachedFromVisualTree(e);
    }
}
