namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private WorkflowEntrySuggestions? CaptureEntrySuggestions() => _localPreferences.EntrySuggestions?.Normalize();
    private void NotifyEntrySuggestions()
    { OnPropertyChanged(nameof(ManagerSuggestions)); OnPropertyChanged(nameof(CustomerSuggestions)); }

    private async Task<string?> SaveEntrySuggestionsAsync(WorkflowEntrySuggestions? expected, WorkflowEntrySuggestions value, CancellationToken token)
    {
        if (Updates.IsApplying) return "Дождитесь завершения обновления.";
        try
        {
            value = value.Normalize();
            var saved = await Task.Run(() => _preferences.Update(current =>
            {
                if (!WorkflowEntrySuggestions.Same(current.EntrySuggestions, expected) && !WorkflowEntrySuggestions.Same(current.EntrySuggestions, value))
                    throw new InvalidOperationException("Списки изменены в другом окне. Ваш ввод сохранён; откройте настройки заново и сравните значения.");
                return WorkflowEntrySuggestions.Same(current.EntrySuggestions, value) ? current : current with { EntrySuggestions = value };
            }), token);
            // Updating local lists must not silently switch the visible working identity.
            _localPreferences = _localPreferences with { EntrySuggestions = saved.EntrySuggestions };
            NotifyEntrySuggestions(); return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return ex is InvalidOperationException or ArgumentException ? ex.Message :
                "Не удалось сохранить локальные списки: файл настроек недоступен, занят или повреждён. Ваш ввод сохранён.";
        }
    }
}
