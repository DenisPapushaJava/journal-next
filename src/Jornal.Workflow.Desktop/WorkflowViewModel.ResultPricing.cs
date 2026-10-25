using Jornal.Workflow.Core;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    public WorkflowResultPricing ResultPricing => _localPreferences.ResultPricing ?? new();

    internal string? SaveResultPricing(WorkflowResultPricing expected, WorkflowResultPricing value)
    {
        if (Updates.IsApplying) return "Дождитесь завершения обновления.";
        if (value != value.Normalize()) return "Минимальная цена — целое неотрицательное число.";
        try
        {
            var saved = _preferences.Update(current =>
            {
                var prior = current.ResultPricing ?? new();
                if (prior != expected && prior != value)
                    throw new InvalidOperationException("Параметры письма изменены в другом окне. Откройте настройки заново.");
                return prior == value ? current : current with { ResultPricing = value };
            });
            _localPreferences = _localPreferences with { ResultPricing = saved.ResultPricing };
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        { return "Не удалось сохранить параметры письма. Прежнее правило сохранено."; }
        catch (InvalidOperationException ex) { return ex.Message; }
    }
}
