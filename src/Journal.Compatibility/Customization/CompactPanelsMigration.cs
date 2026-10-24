using Jornal.Infrastructure.Diagnostics;

namespace Jornal.Desktop.Customization;

/// <summary>Единственный автоматический переход к принятому макету. Номер релиза здесь не участвует.</summary>
public static class CompactPanelsMigration
{
    public static PreferencesLoadResult Apply(UiPreferences preferences, Action<UiPreferences> persist)
    {
        if (preferences.Layout.CompactPanelsPresetApplied) return new(preferences);
        var migrated = preferences with { Layout = preferences.Layout with
            { Workspace = WorkspaceLayout.CreateDefault().Normalize(), CompactPanelsPresetApplied = true } };
        try
        {
            // Отметка и компоновка записываются вместе до показа нового вида.
            persist(migrated);
            return new(migrated);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            AppDiagnostics.Capture(DiagnosticOperation.Preferences, exception);
            return new(preferences, "Не удалось сохранить новую компоновку. Используется прежняя.");
        }
    }
}
