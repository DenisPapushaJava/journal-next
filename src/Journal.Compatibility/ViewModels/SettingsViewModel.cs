using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.ViewModels;

public enum SettingsSection { General, Database, Updates, Diagnostics, About }

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    public MainWindowViewModel Journal { get; }
    public DatabaseSettingsViewModel? Database { get; }
    public AppUpdatesViewModel Updates => Journal.Updates;
    public DiagnosticsViewModel? Diagnostics { get; }
    public bool HasDatabase => Database is not null;
    public bool HasDiagnostics => Diagnostics is not null;
    public bool IsGeneral => SelectedIndex == (int)SettingsSection.General;
    public bool IsDatabase => SelectedIndex == (int)SettingsSection.Database;
    public bool IsUpdates => SelectedIndex == (int)SettingsSection.Updates;
    public bool IsDiagnostics => SelectedIndex == (int)SettingsSection.Diagnostics;
    public bool IsAbout => SelectedIndex == (int)SettingsSection.About;
    public bool CanNavigate => Database is not { CanChangeSource: false } && !Updates.IsApplying && Diagnostics is not { IsExporting: true };
    public bool CanClose => (!Updates.IsApplying || Updates.RestartScheduled) && Diagnostics is not { IsExporting: true };
    public bool CanRestart => CanNavigate;
    public bool CanCustomize => CanNavigate && Journal.CanConfigureScreen;

    [ObservableProperty] private string? _linkError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGeneral), nameof(IsDatabase), nameof(IsUpdates), nameof(IsDiagnostics), nameof(IsAbout))]
    private int _selectedIndex;

    public SettingsViewModel(MainWindowViewModel journal, DiagnosticService? diagnostics = null,
        SettingsSection section = SettingsSection.General)
    {
        Journal = journal;
        Database = journal.CreateDatabaseSettingsForm();
        Diagnostics = diagnostics is null ? null : new(diagnostics);
        _selectedIndex = (int)section;
        AcknowledgeDiagnostics();
        Journal.PropertyChanged += OnStateChanged;
        Updates.PropertyChanged += OnStateChanged;
        if (Database is not null) Database.PropertyChanged += OnStateChanged;
        if (Diagnostics is not null) Diagnostics.PropertyChanged += OnStateChanged;
    }

    partial void OnSelectedIndexChanged(int value) => AcknowledgeDiagnostics();
    private void AcknowledgeDiagnostics()
    {
        if (IsDiagnostics && Diagnostics?.Service.HasPreviousFatal == true) Diagnostics.Service.AcknowledgePreviousFatal();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, Journal) && e.PropertyName == nameof(MainWindowViewModel.SourceDescription) && Database is not null)
            Database.CurrentSource = Journal.SourceDescription;
        OnPropertyChanged(nameof(CanNavigate)); OnPropertyChanged(nameof(CanClose));
        OnPropertyChanged(nameof(CanRestart)); OnPropertyChanged(nameof(CanCustomize));
    }

    public void Dispose()
    {
        Journal.PropertyChanged -= OnStateChanged;
        Updates.PropertyChanged -= OnStateChanged;
        if (Database is not null) Database.PropertyChanged -= OnStateChanged;
        if (Diagnostics is not null) Diagnostics.PropertyChanged -= OnStateChanged;
    }
}
