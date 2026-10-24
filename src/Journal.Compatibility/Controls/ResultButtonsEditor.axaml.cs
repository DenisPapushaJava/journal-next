using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Jornal.Desktop.Customization;

namespace Jornal.Desktop.Controls;

public partial class ResultButtonsEditor : UserControl
{
    private enum Role { Result, Copy, Clear }
    private readonly ColorWheel _wheel = new() { Width = 200, Height = 200, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
    private readonly TextBlock _paletteTitle = new() { FontWeight = FontWeight.SemiBold };
    private readonly Flyout _palette;
    private Role _role;
    private bool _settingWheel;
    public ResultButtonAppearance Value { get; private set; } = new();
    public event EventHandler<ResultButtonAppearance>? AppearanceChanged;
    internal bool HasOpenPopup => ResultButtonsSettingsButton.Flyout?.IsOpen == true || _palette.IsOpen;

    public ResultButtonsEditor()
    {
        InitializeComponent();
        var swatches = new UniformGrid { Columns = 8 };
        _palette = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = new StackPanel { Width = 220, Spacing = 10, Children = { _paletteTitle, swatches, _wheel } } };
        foreach (var hex in new[] { "#D6E1EE", "#35689B", "#A94A54", "#4D7C60", "#9A7436", "#766394", "#FFFFFF", "#202A36" })
        {
            var button = new Button { Width = 24, Height = 24, MinHeight = 24, Padding = new Thickness(0), Margin = new Thickness(1),
                CornerRadius = new CornerRadius(4), Background = new ImmutableSolidColorBrush(Color.Parse(hex)),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            ToolTip.SetTip(button, hex); Avalonia.Automation.AutomationProperties.SetName(button, hex);
            button.Click += (_, _) => { ChangeColor(hex); _palette.Hide(); };
            swatches.Children.Add(button);
        }
        foreach (var button in new[] { ResultActionColorButton, CopyActionColorButton, ClearActionColorButton })
            FlyoutBase.SetAttachedFlyout(button, _palette);
        Avalonia.Automation.AutomationProperties.SetName(_wheel, "Палитра цвета кнопки");
        _wheel.PropertyChanged += OnWheelChanged;
        ResultButtonsSettingsButton.Flyout!.Closed += (_, _) => _palette.Hide();
        SetValue(new());
    }

    public void SetValue(ResultButtonAppearance value)
    {
        Value = value.Normalize();
        ResultButtonsSizeValue.Text = Value.Size.ToString(CultureInfo.InvariantCulture) + " px";
        DecreaseResultButtonsButton.IsEnabled = Value.Size > ResultButtonAppearance.MinimumSize;
        IncreaseResultButtonsButton.IsEnabled = Value.Size < ResultButtonAppearance.MaximumSize;
        ResultActionColorSample.Background = new ImmutableSolidColorBrush(Color.Parse(Value.ResultColor));
        CopyActionColorSample.Background = new ImmutableSolidColorBrush(Color.Parse(Value.CopyColor));
        ClearActionColorSample.Background = new ImmutableSolidColorBrush(Color.Parse(Value.ClearColor));
    }

    public void ClosePopups() { _palette.Hide(); ResultButtonsSettingsButton.Flyout?.Hide(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { ClosePopups(); base.OnDetachedFromVisualTree(e); }
    private void Change(ResultButtonAppearance value) { SetValue(value); AppearanceChanged?.Invoke(this, Value); }
    private void OnDecrease(object? sender, RoutedEventArgs e) => Change(Value with { Size = Value.Size - 1 });
    private void OnIncrease(object? sender, RoutedEventArgs e) => Change(Value with { Size = Value.Size + 1 });
    private void OnReset(object? sender, RoutedEventArgs e) => Change(new());
    private void OnResultColor(object? sender, RoutedEventArgs e) => OpenPalette(ResultActionColorButton, Role.Result, "Результат", Value.ResultColor);
    private void OnCopyColor(object? sender, RoutedEventArgs e) => OpenPalette(CopyActionColorButton, Role.Copy, "Копировать", Value.CopyColor);
    private void OnClearColor(object? sender, RoutedEventArgs e) => OpenPalette(ClearActionColorButton, Role.Clear, "Очистить", Value.ClearColor);
    private void OpenPalette(Button button, Role role, string label, string hex)
    {
        _role = role; _paletteTitle.Text = $"Цвет кнопки «{label}»";
        _settingWheel = true;
        try { _wheel.HsvColor = new HsvColor(Color.Parse(hex)); }
        finally { _settingWheel = false; }
        FlyoutBase.ShowAttachedFlyout(button);
    }
    private void OnWheelChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ColorWheel.HsvColorProperty && !_settingWheel && _palette.IsOpen)
            ChangeColor("#" + (_wheel.HsvColor.ToRgb().ToUInt32() & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture));
    }
    private void ChangeColor(string hex) => Change(_role switch
    {
        Role.Result => Value with { ResultColor = hex },
        Role.Copy => Value with { CopyColor = hex },
        _ => Value with { ClearColor = hex }
    });
}
