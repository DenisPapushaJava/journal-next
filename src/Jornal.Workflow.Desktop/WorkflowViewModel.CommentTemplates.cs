using System.Text;
using System.Text.RegularExpressions;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    internal string CommentTemplatesText => _localPreferences.CommentTemplates ?? "";

    internal static string[] CommentTemplateLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray();

    internal static string ApplyCommentTemplateChanges(string? comment, WorkflowCommentTemplateChanges changes)
    {
        var text = comment ?? "";
        if (changes.Removed.Length > 0)
        {
            var removed = changes.Removed.ToHashSet(StringComparer.Ordinal);
            var lines = Regex.Split(text, @"(\r\n|[\r\n])");
            var remaining = new StringBuilder(text.Length);
            var separator = "";
            for (var index = 0; index < lines.Length; index += 2)
            {
                if (removed.Contains(lines[index].Trim())) continue;
                remaining.Append(separator).Append(lines[index]);
                separator = index + 1 < lines.Length ? lines[index + 1] : "";
            }
            text = remaining.ToString();
        }

        var added = string.Join('\n', changes.Added.Except(CommentTemplateLines(text), StringComparer.Ordinal));
        if (added.Length == 0) return text;
        return string.IsNullOrWhiteSpace(text) ? added : text.TrimEnd('\r', '\n') + "\n" + added;
    }

    internal async Task<string?> SaveCommentTemplatesAsync(string expected, string text)
    {
        if (Updates.IsApplying) return "Дождитесь завершения обновления.";
        var templates = string.Join('\n', CommentTemplateLines(text));
        try
        {
            var saved = await Task.Run(() => _preferences.Update(current =>
            {
                var prior = current.CommentTemplates ?? "";
                if (prior == templates) return current;
                if (prior != expected)
                    throw new InvalidOperationException("Шаблоны изменены в другом окне. Откройте настройки заново.");
                return current with { CommentTemplates = templates.Length == 0 ? null : templates };
            }));
            _localPreferences = _localPreferences with { CommentTemplates = saved.CommentTemplates };
            return null;
        }
        catch (InvalidOperationException ex) { return ex.Message; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return ex is InvalidDataException ? ex.Message : "Не удалось сохранить шаблоны. Проверьте доступ к локальным настройкам.";
        }
    }
}
