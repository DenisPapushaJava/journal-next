using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowWindow
{
    private bool _openingOfflineFolder;

    private async void OnOfflineOfficeFolders(object? sender, RoutedEventArgs e)
    {
        if (_openingOfflineFolder || OwnedWindows.Count > 0 ||
            Model is not { CanUse: true, HasOfflineOfficeFolders: true } model) return;
        var token = _lifetime.Token;
        bool IsCurrent() => IsVisible && !token.IsCancellationRequested && ReferenceEquals(Model, model) &&
            model.HasOfflineOfficeFolders;
        _openingOfflineFolder = true;
        try
        {
            if (await model.RefreshOfflineOfficeFoldersAsync(token) is not { Items.Length: > 0 } folders || !IsCurrent()) return;
            var window = new WorkflowOfflineFoldersWindow(folders) { RequestedThemeVariant = ActualThemeVariant };
            if (await window.ShowDialog<WorkflowOfflineFolder?>(this) is not { } selected || !IsCurrent()) return;
            var path = await model.VerifyOfflineOfficeFolderAsync(selected, token);
            if (!IsCurrent()) return;
            if (path is null || !await OpenFolderAsync(path))
            {
                if (IsCurrent()) model.Error = "Нет связи с офисом. Эту папку открыть не удалось. Проверьте доступ и откройте список снова.";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (IsCurrent()) model.Error = "Нет связи с офисом. Не удалось открыть скачанную папку.";
            Jornal.Infrastructure.Diagnostics.AppDiagnostics.Capture(Jornal.Infrastructure.Diagnostics.DiagnosticOperation.Unexpected, ex);
        }
        finally
        {
            _openingOfflineFolder = false;
            if (IsCurrent() && sender is Control button) button.Focus();
        }
    }

    private async void OnOpenOfflineJournalProject(object? sender, RoutedEventArgs e)
    {
        if (_openingOfflineFolder || OwnedWindows.Count > 0 || Model is not { CanUse: true, OfflineJournal: { } journal } model ||
            sender is not Control { DataContext: WorkflowOfflineProjectRow project }) return;
        var token = _lifetime.Token;
        bool IsCurrent() => IsVisible && ReferenceEquals(Model, model) && ReferenceEquals(model.OfflineJournal, journal) && !token.IsCancellationRequested;
        _openingOfflineFolder = true;
        try
        {
            var folder = await model.VerifyOfflineJournalProjectAsync(project, token);
            if (!IsCurrent()) return;
            if (folder is null || !await OpenFolderAsync(folder)) model.Error = "Скачанная папка проекта недоступна. Проверьте её расположение.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { if (IsCurrent()) model.Error = "Не удалось открыть скачанную папку проекта."; }
        finally { _openingOfflineFolder = false; }
    }
}
