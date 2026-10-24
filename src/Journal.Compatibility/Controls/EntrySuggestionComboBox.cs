using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Jornal.Desktop.Controls;

// Keeps the native editable ComboBox template; previewing a suggestion does not commit it.
public sealed class EntrySuggestionComboBox : ComboBox
{
    public static readonly StyledProperty<IReadOnlyList<string>?> SuggestionsProperty =
        AvaloniaProperty.Register<EntrySuggestionComboBox, IReadOnlyList<string>?>(nameof(Suggestions));
    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<EntrySuggestionComboBox, string?>(nameof(Value), "", defaultBindingMode: BindingMode.TwoWay);
    private int _syncDepth;
    private bool _filtering;
    private int _previewIndex = -1;
    private TextBox? _input;
    private string? _completion;
    private (int Start, int End)? _caretOnClose;

    protected override Type StyleKeyOverride => typeof(ComboBox);
    public IReadOnlyList<string>? Suggestions
    { get => GetValue(SuggestionsProperty); set => SetValue(SuggestionsProperty, value); }
    public string? Value
    { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public EntrySuggestionComboBox()
    {
        IsEditable = true; IsTextSearchEnabled = false;
        // Navigate suggestions before the TextBox consumes arrows to collapse its selection.
        AddHandler(KeyDownEvent, OnSuggestionKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnEditorTextInput, RoutingStrategies.Bubble, handledEventsToo: true);
        ContainerPrepared += (_, e) =>
        {
            // Keep typing in the editor even when the text exactly matches a saved name.
            e.Container.Focusable = false;
            e.Container.Classes.Set("suggestion-preview", e.Index == _previewIndex);
        };
        DropDownOpened += (_, _) =>
        {
            if (!_filtering) RefreshItems(filter: false);
            Preview(_previewIndex);
        };
        DropDownClosed += (_, _) =>
        {
            _filtering = false; Preview(-1);
            if (_caretOnClose is { } caret && _input is not null)
            { _input.SelectionStart = caret.Start; _input.SelectionEnd = caret.End; }
            _caretOnClose = null;
        };
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _input = e.NameScope.Find<TextBox>("PART_EditableTextBox");
        RefreshItems(filter: _filtering);
    }

    protected override void OnDataContextBeginUpdate()
    {
        _completion = null;
        _syncDepth++;
        base.OnDataContextBeginUpdate();
    }

    protected override void OnDataContextEndUpdate()
    {
        try { base.OnDataContextEndUpdate(); }
        finally { _syncDepth--; }
        RefreshItems(filter: false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == SelectedItemProperty && _syncDepth == 0)
        {
            _completion = null;
            _syncDepth++;
            try { base.OnPropertyChanged(change); SetCurrentValue(ValueProperty, Text ?? ""); }
            finally { _syncDepth--; }
            return;
        }
        base.OnPropertyChanged(change);
        if (change.Property == IsEffectivelyEnabledProperty && !IsEffectivelyEnabled)
        {
            RestoreTypedText();
            SetCurrentValue(IsDropDownOpenProperty, false);
        }
        if (_syncDepth > 0) return;
        if (change.Property == SuggestionsProperty || change.Property == ValueProperty)
        {
            _syncDepth++;
            try { RefreshItems(filter: false); }
            finally { _syncDepth--; }
        }
        else if (change.Property == TextProperty)
        {
            _completion = null;
            _syncDepth++;
            try { SetCurrentValue(ValueProperty, Text ?? ""); }
            finally { _syncDepth--; }
            if (!IsKeyboardFocusWithin || !IsEffectivelyVisible || !IsEffectivelyEnabled) return;
            _filtering = true;
            RefreshItems(filter: true);
            if (ItemCount == 0 && IsDropDownOpen && _input is not null)
                _caretOnClose = (_input.SelectionStart, _input.SelectionEnd);
            SetCurrentValue(IsDropDownOpenProperty, ItemCount > 0);
        }
    }

    private void RefreshItems(bool filter)
    {
        _completion = null;
        _syncDepth++;
        var text = Value ?? "";
        var caret = (_input?.SelectionStart ?? 0, _input?.SelectionEnd ?? 0);
        try
        {
            var items = (Suggestions ?? []).Where(item => !filter || item.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            if (!ItemsView.Cast<string>().SequenceEqual(items)) SetCurrentValue(ItemsSourceProperty, items);
            SetCurrentValue(TextProperty, text);
            _input?.SetCurrentValue(TextBox.TextProperty, text);
            if (_input is not null) { _input.SelectionStart = caret.Item1; _input.SelectionEnd = caret.Item2; }
            var exact = Array.FindIndex(items, item => string.Equals(item, text, StringComparison.CurrentCultureIgnoreCase));
            Preview(exact >= 0 ? exact : items.Length > 0 ? 0 : -1);
        }
        finally { _syncDepth--; }
    }

    private void Preview(int index)
    {
        _previewIndex = index;
        foreach (var container in GetRealizedContainers())
            container.Classes.Set("suggestion-preview", IndexFromContainer(container) == index);
        if (index >= 0 && IsDropDownOpen) ScrollIntoView(index);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        // Native ComboBox redirects every descendant's focus to its editor, including inner buttons.
        if (ReferenceEquals(e.Source, this) || ReferenceEquals(e.Source, _input)) base.OnGotFocus(e);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, _input) && _syncDepth == 0 && IsEffectivelyVisible && IsEffectivelyEnabled)
            AcceptCompletion();
        base.OnLostFocus(e);
    }

    private void OnEditorTextInput(object? sender, TextInputEventArgs e)
    {
        // TextBox has finished editing/caret updates by the time its handled input bubbles here.
        if (!ReferenceEquals(e.Source, _input) || string.IsNullOrEmpty(e.Text) ||
            _input is not { IsFocused: true, IsReadOnly: false } ||
            !IsEffectivelyVisible || !IsEffectivelyEnabled || _syncDepth != 0) return;
        if (_completion is not null)
        {
            // Typing exactly the displayed suffix may leave Text unchanged, but it is now user input.
            _completion = null;
            SetCurrentValue(ValueProperty, _input.Text ?? "");
        }
        if (_input.SelectionStart == _input.SelectionEnd && _input.CaretIndex == (Value?.Length ?? 0))
        {
            if (!_filtering)
            {
                _filtering = true; RefreshItems(filter: true);
                SetCurrentValue(IsDropDownOpenProperty, ItemCount > 0);
            }
            ShowCompletion();
        }
    }

    private void ShowCompletion()
    {
        var prefix = Value ?? "";
        if (_input is null || prefix.Length == 0 || _previewIndex < 0 || _previewIndex >= ItemCount) return;
        var chosen = (string)ItemsView[_previewIndex]!;
        if (chosen.Length <= prefix.Length || !chosen.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)) return;
        _syncDepth++;
        try
        {
            _completion = chosen;
            var display = prefix + chosen[prefix.Length..];
            SetEditorText(display);
            _input.SelectionStart = prefix.Length; _input.SelectionEnd = display.Length;
        }
        finally { _syncDepth--; }
    }

    private void RestoreTypedText()
    {
        if (_completion is null) return;
        _completion = null;
        _syncDepth++;
        try
        {
            var text = Value ?? "";
            SetEditorText(text);
            if (_input is not null) _input.SelectionStart = _input.SelectionEnd = text.Length;
        }
        finally { _syncDepth--; }
    }

    private void AcceptCompletion()
    {
        if (_completion is { } chosen) Commit(chosen);
    }

    private void Commit(string chosen)
    {
        _completion = null;
        _syncDepth++;
        try
        {
            SetEditorText(chosen);
            SetCurrentValue(SelectedItemProperty, chosen);
            SetCurrentValue(ValueProperty, chosen);
        }
        finally { _syncDepth--; }
    }

    private void SetEditorText(string text)
    {
        // SelectedText uses a native edit, preserving undo; external Text replacement clears it.
        if (_input is not null && _input.Text != text)
        { _input.SelectAll(); _input.SelectedText = text; }
        SetCurrentValue(TextProperty, text);
    }

    public override bool UpdateSelectionFromEvent(Control container, RoutedEventArgs eventArgs)
    {
        var index = IndexFromContainer(container);
        var accepted = base.UpdateSelectionFromEvent(container, eventArgs);
        // Clicking the already highlighted item may leave SelectedItem unchanged.
        if (accepted && eventArgs.RoutedEvent == PointerReleasedEvent && index >= 0 && index < ItemCount)
            Commit((string)ItemsView[index]!);
        return accepted;
    }

    private void OnSuggestionKeyDown(object? sender, KeyEventArgs e)
    {
        // Inner buttons (material confirmation) keep their own keyboard actions.
        if (!ReferenceEquals(e.Source, this) && !ReferenceEquals(e.Source, _input)) return;
        if (!e.Handled && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key is Key.Down or Key.Up)
            {
                var completing = _completion is not null;
                RestoreTypedText();
                if (!IsDropDownOpen) SetCurrentValue(IsDropDownOpenProperty, ItemCount > 0);
                else if (ItemCount > 0) Preview(Math.Clamp(_previewIndex + (e.Key == Key.Down ? 1 : -1), 0, ItemCount - 1));
                if (completing) ShowCompletion();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _completion is not null)
            {
                RestoreTypedText();
                _caretOnClose = ((Value ?? "").Length, (Value ?? "").Length);
                SetCurrentValue(IsDropDownOpenProperty, false); e.Handled = true;
            }
            else if (e.Key is Key.Tab or Key.Right or Key.End)
                AcceptCompletion();
            else if (e.Key == Key.Left && _completion is not null)
            {
                RestoreTypedText(); e.Handled = true;
            }
            else if (e.Key == Key.Home)
                RestoreTypedText();
            else if (e.Key == Key.Enter && (IsDropDownOpen || _completion is not null))
            {
                if (_previewIndex >= 0 && _previewIndex < ItemCount)
                    Commit((string)ItemsView[_previewIndex]!);
                else AcceptCompletion();
                SetCurrentValue(IsDropDownOpenProperty, false);
                e.Handled = true;
            }
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        RestoreTypedText();
        SetCurrentValue(IsDropDownOpenProperty, false);
        _caretOnClose = null;
        base.OnDetachedFromVisualTree(e);
    }
}
