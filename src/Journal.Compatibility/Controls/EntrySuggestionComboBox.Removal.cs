using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Jornal.Desktop.Controls;

public sealed partial class EntrySuggestionComboBox
{
    public static readonly StyledProperty<ICommand?> RemoveSuggestionCommandProperty =
        AvaloniaProperty.Register<EntrySuggestionComboBox, ICommand?>(nameof(RemoveSuggestionCommand));
    public ICommand? RemoveSuggestionCommand
    { get => GetValue(RemoveSuggestionCommandProperty); set => SetValue(RemoveSuggestionCommandProperty, value); }

    private void UpdateRemovalTemplate()
    {
        ItemTemplate = RemoveSuggestionCommand is null ? null : new FuncDataTemplate<string>((name, _) =>
        {
            var grid = new Grid { ColumnDefinitions = new("*,8,28") };
            grid.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var remove = new JournalButton
            {
                Icon = "x", IconOnly = true, Label = $"Убрать «{name}» из списка", Width = 28, Height = 28, MinHeight = 28,
                Padding = new(0), VerticalAlignment = VerticalAlignment.Center,
                Command = RemoveSuggestionCommand, CommandParameter = name
            };
            remove.Classes.Add("command"); remove.Classes.Add("suggestion-remove");
            AutomationProperties.SetName(remove, remove.Label); ToolTip.SetTip(remove, remove.Label);
            // Preserve typed text before focus moves into the row action; a remove click is never a selection.
            remove.AddHandler(PointerPressedEvent, (_, _) => RestoreTypedText(), RoutingStrategies.Tunnel);
            remove.Click += (_, _) => SetCurrentValue(IsDropDownOpenProperty, false);
            remove.AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key is not (Key.Enter or Key.Space) || e.Handled) return;
                e.Handled = true;
                if (!remove.IsEffectivelyEnabled || remove.Command?.CanExecute(name) != true) return;
                RestoreTypedText(); SetCurrentValue(IsDropDownOpenProperty, false); remove.Command.Execute(name);
            }, RoutingStrategies.Tunnel);
            Grid.SetColumn(remove, 2); grid.Children.Add(remove); return grid;
        });
    }

    private static bool IsRemovalSource(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is Button button && button.Classes.Contains("suggestion-remove")) return true;
        return false;
    }
}
