using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Jornal.Workflow.Desktop;

/// <summary>A compact, read-only label. Its meaning and tooltip belong to the caller.</summary>
public sealed class WorkflowBadge : TemplatedControl
{
    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<WorkflowBadge, string>(nameof(Label), "");
    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<WorkflowBadge, Geometry?>(nameof(Icon));
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Geometry? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty) PseudoClasses.Set(":icon", Icon is not null);
    }
}

public sealed record WorkflowBadgeItem(string Label, string Hint, string? IconPath = null);
