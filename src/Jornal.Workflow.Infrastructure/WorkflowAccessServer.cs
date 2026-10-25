using System.Runtime.InteropServices;
using System.Text.Json;
using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public static class WorkflowAccessServer
{
    public static async Task<int> RunAsync()
    {
        if (!OperatingSystem.IsWindows() || Environment.Is64BitProcess) return 2;
        WorkflowAccessResponse response;
        try
        {
            var text = await Console.In.ReadLineAsync();
            if (text is null || text.Length > 32 * 1024 * 1024) throw new WorkflowValidationException("Не передан допустимый запрос MDB.");
            var request = JsonSerializer.Deserialize<WorkflowAccessRequest>(text) ?? throw new WorkflowValidationException("Пустой запрос MDB.");
            response = new WorkflowMdbDatabase().Execute(request);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            var message = e switch
            {
                WorkflowConflictException or WorkflowValidationException or WorkflowOutcomeUnknownException => e.Message,
                COMException => "Недоступен системный компонент создания MDB (Jet/ADOX x86). Код: " + e.HResult.ToString("X8"),
                System.Data.OleDb.OleDbException => "База MDB занята либо запрос не выполнен. Проверьте доступ и обновите журнал. Код: " + e.HResult.ToString("X8"),
                UnauthorizedAccessException => "Нет прав на работу с файлом MDB или его папкой.",
                IOException => "Файл MDB или общая папка недоступны. Проверьте подключение и права.",
                _ => "Не удалось выполнить операцию MDB. Код: " + e.HResult.ToString("X8")
            };
            response = new(1, Error: message, Conflict: e is WorkflowConflictException, OutcomeUnknown: e is WorkflowOutcomeUnknownException);
        }
        await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), response); return 0;
    }
}
