using System.Text.Json;

namespace Jornal.Workflow.Desktop;

public sealed record WorkflowProfileDraft(Guid EmployeeId, string Name, uint ColorArgb = 0xFF628DB2);
public sealed record WorkflowPreferences(bool DarkTheme = false)
{
    public Guid? EmployeeId { get; init; }
    public WorkflowEntrySuggestions? EntrySuggestions { get; init; }
    public void Validate()
    {
        if (EmployeeId == Guid.Empty) throw new InvalidDataException("Проверьте локальный выбор технолога.");
        try { EntrySuggestions?.Normalize(); }
        catch (ArgumentException ex) { throw new InvalidDataException("Проверьте локальные списки ввода: " + ex.Message, ex); }
    }
}
public sealed class WorkflowPreferencesStore(string path)
{
    public const int MaximumBytes = 128 * 1024;
    public string DirectoryPath => Path.GetDirectoryName(Path.GetFullPath(path))!;
    public WorkflowPreferences Load()
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("Настройки нового Journal слишком велики.");
            var value = JsonSerializer.Deserialize<WorkflowPreferences>(stream) ?? new(); value.Validate();
            return value with { EntrySuggestions = value.EntrySuggestions?.Normalize() };
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new(); }
    }
    public void Save(WorkflowPreferences preferences)
    { Update(_ => preferences); }

    public WorkflowPreferences Update(Func<WorkflowPreferences, WorkflowPreferences> change)
    {
        Directory.CreateDirectory(DirectoryPath);
        using var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = Load(); var next = change(current); next.Validate();
        if (ReferenceEquals(current, next)) return current;
        next = next with { EntrySuggestions = next.EntrySuggestions?.Normalize() }; SaveCore(next); return next;
    }

    private void SaveCore(WorkflowPreferences preferences)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, preferences);
                if (stream.Length > MaximumBytes) throw new InvalidDataException("Настройки нового Journal слишком велики. Прежний файл сохранён.");
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record WorkflowRestartResult(Guid OrderId, long Version, string Text, bool Expanded, bool Stale);
public sealed record WorkflowRestartDraft(int Schema, Guid? OrderId, string ResultText)
{
    public WorkflowRestartResult[]? Results { get; init; }
}
public sealed class WorkflowRestartStore(string path)
{
    public void Save(WorkflowRestartDraft draft)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, draft);
                if (stream.Length > 2 * 1024 * 1024) throw new IOException("Результат слишком велик для сохранения перед обновлением.");
                stream.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public WorkflowRestartDraft? Take()
    {
        try
        {
            WorkflowRestartDraft draft;
            using (var stream = File.OpenRead(path))
            {
                if (stream.Length > 2 * 1024 * 1024) throw new InvalidDataException("Слишком большой черновик обновления.");
                draft = JsonSerializer.Deserialize<WorkflowRestartDraft>(stream) ?? throw new InvalidDataException("Черновик обновления пуст.");
                if (draft.Schema != 1 || draft.ResultText is null) throw new InvalidDataException("Неизвестный черновик обновления.");
                if (draft.Results is { } results && (results.Length > 500 || results.Any(r => r is null || r.OrderId == Guid.Empty ||
                    r.Version < 0 || r.Text is null) || results.Select(r => r.OrderId).Distinct().Count() != results.Length))
                    throw new InvalidDataException("Повреждены результаты карточек перед обновлением.");
            }
            File.Delete(path); return draft;
        }
        catch (FileNotFoundException) { return null; }
    }
}
