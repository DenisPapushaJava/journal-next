using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public static class WorkflowBendingFiles
{
    public static Task<string> EnsureAsync(CuttingProject project, CancellationToken token = default) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        if (project.IsDeleted || project.Rename is not null)
            throw new WorkflowValidationException("Сначала восстановите проект или завершите изменение его параметров.");
        if (project.NativePath is not { } native || !Path.IsPathFullyQualified(native) || Path.GetDirectoryName(native) is not { } folder)
            throw new WorkflowValidationException("У проекта не задана рабочая папка.");
        // Never recreate a missing working project or follow a substituted directory link.
        var attributes = File.GetAttributes(folder);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0 || !File.Exists(native))
            throw new WorkflowValidationException("Рабочая папка или файл проекта недоступны. Проверьте их расположение.");
        var path = Path.Combine(folder, "ГИБКА");
        if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new WorkflowValidationException("Вместо папки ГИБКА обнаружена ссылка. Проверьте рабочую папку проекта.");
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        return path;
    }, token);
}
