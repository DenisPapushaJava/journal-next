namespace Jornal.Desktop.Updates;

public static class AppUpdateSafety
{
    public static void EnsureExternalDatabase(string? databasePath, string applicationDirectory)
    {
        if (databasePath is null) return;
        var relative = Path.GetRelativePath(Path.GetFullPath(applicationDirectory), Path.GetFullPath(databasePath));
        if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            throw new InvalidOperationException("MDB находится в заменяемой папке приложения. Перед обновлением разместите базу в отдельной папке и выберите её через «База данных». Автоматически база не перемещается.");
    }
}
