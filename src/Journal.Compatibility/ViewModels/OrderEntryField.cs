using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Jornal.Desktop.ViewModels;

public partial class OrderEntryField(string label, string value = "",
    IReadOnlyList<string>? choices = null) : ObservableObject
{
    public string Label { get; } = label;
    [ObservableProperty] private bool _isInputEnabled = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChoices)), NotifyPropertyChangedFor(nameof(HasSuggestions)), NotifyPropertyChangedFor(nameof(HasEditableSuggestions)), NotifyPropertyChangedFor(nameof(EditableSuggestions)), NotifyPropertyChangedFor(nameof(HasPlainInput)), NotifyPropertyChangedFor(nameof(SelectedChoice)), NotifyPropertyChangedFor(nameof(UnlistedChoice))] private IReadOnlyList<string>? _choices = choices;
    public bool HasChoices => Choices is not null;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSuggestions)), NotifyPropertyChangedFor(nameof(HasEditableSuggestions)), NotifyPropertyChangedFor(nameof(EditableSuggestions)), NotifyPropertyChangedFor(nameof(HasPlainInput))] private IReadOnlyList<string>? _suggestions;
    public bool UseEditableSuggestions { get; init; }
    public bool HasSuggestions => !HasChoices && !UseEditableSuggestions && Suggestions is { Count: > 0 };
    public bool HasEditableSuggestions => !HasChoices && UseEditableSuggestions && Suggestions is { Count: > 0 };
    public IReadOnlyList<string>? EditableSuggestions => HasEditableSuggestions ? Suggestions : null;
    public bool HasPlainInput => !HasChoices && !HasSuggestions && !HasEditableSuggestions;
    public ThicknessOptionsViewModel? ThicknessOptions { get; internal set; }
    public bool HasThicknessOptions => ThicknessOptions is not null;
    [ObservableProperty] private bool _isChoiceLoading;
    public string? SelectedChoice
    {
        get => ThicknessOptions is null ? Value : Choices?.FirstOrDefault(choice => SameThickness(choice, Value));
        set
        {
            if (ThicknessOptions is null) { Value = value; return; }
            // ItemsSource/пересоздание контейнера могут сбросить выбор: исходная толщина при этом не теряется.
            if (value is null || Choices?.Contains(value) != true || SameThickness(value, Value)) return;
            Value = value;
        }
    }
    public string UnlistedChoice => HasThicknessOptions && SelectedChoice is null ? Value : "";
    private static bool SameThickness(string first, string second) =>
        decimal.TryParse(first.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var a) &&
        decimal.TryParse(second.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var b) && a == b;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayError)), NotifyPropertyChangedFor(nameof(HasDisplayError))] private string? _choiceError;
    public string? DisplayError => Error ?? ChoiceError;
    public bool HasDisplayError => DisplayError is not null;
    public string? SuggestionText { get => Value; set => Value = value ?? ""; }
    private string? _importedValue;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasImportHint)), NotifyPropertyChangedFor(nameof(InputHint))] private string? _importHint;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(InputHint))] private string? _importEvidence;
    public bool HasImportHint => _importedValue == "" && Value.Length == 0;
    public string? InputHint => ImportEvidence is { Length: > 0 } ? $"{ImportHint}\n{ImportEvidence}" : Value;
    public void ApplyImport(string value, string evidence)
    {
        NeedsReview = false;
        Value = value; _importedValue = value; ImportEvidence = evidence;
        ImportHint = value.Length == 0 ? "Не распознано" : "Из спецификации";
        Error = null;
    }
    private void OnValueChanged(string value)
    {
        NeedsReview = false;
        if (_importedValue is not null) ImportHint = value == _importedValue
            ? value.Length == 0 ? "Не распознано" : "Из спецификации" : "Изменено вручную";
    }
    [ObservableProperty] private bool _needsReview;
    [RelayCommand] private void ConfirmReview() { NeedsReview = false; Error = null; }
    private string _value = value ?? "";
    // TextBox.Text и ComboBox.SelectedItem могут прислать null до/при пересоздании привязки.
    [AllowNull]
    public string Value
    {
        get => _value;
        set
        {
            var text = value ?? "";
            if (_value == text) return;
            OnPropertyChanging(nameof(Value));
            _value = text;
            OnValueChanged(text);
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(HasImportHint));
            OnPropertyChanged(nameof(SuggestionText));
            OnPropertyChanged(nameof(SelectedChoice));
            OnPropertyChanged(nameof(UnlistedChoice));
        }
    }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError)), NotifyPropertyChangedFor(nameof(DisplayError)), NotifyPropertyChangedFor(nameof(HasDisplayError))] private string? _error;
    public bool HasError => Error is not null;
}
