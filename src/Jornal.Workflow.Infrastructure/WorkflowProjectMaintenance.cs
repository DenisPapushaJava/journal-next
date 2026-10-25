using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public enum ProjectFileFailureKind { Busy, AccessDenied, Missing, Collision, Unsafe, Incomplete, Unavailable }

public sealed class ProjectFileFailure(ProjectFileFailureKind kind, string message, string path, Exception? inner = null)
    : Exception(message, inner)
{
    public ProjectFileFailureKind Kind { get; } = kind;
    public string ProjectPath { get; } = path;
}

// The intent is durable before touching files. Workspace locks never span filesystem moves.
public sealed class WorkflowProjectMaintenance(IWorkflowWorkspaceStore store, Action<string, string>? moveDirectory = null)
{
    private const string Receipt = ".workflow-project";
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task<WorkflowWorkspace> RenameAsync(Guid orderId, long expectedVersion, Guid projectId,
        Guid machineId, decimal thickness, Guid actor, CancellationToken token = default)
    {
        var book = await store.ReadOrderAsync(orderId, token).ConfigureAwait(false);
        var order = book.Orders.Single(o => o.Id == orderId);
        if (order.Version != expectedVersion) throw new WorkflowConflictException();
        var project = order.Projects.Single(p => p.Id == projectId);
        // Domain preflight, including deleted/calculated projects, without persisting the proposed intent yet.
        var proposed = WorkflowProjects.BeginRename(book, orderId, order.Version, projectId, machineId, thickness, actor, DateTimeOffset.UtcNow);
        if (ReferenceEquals(proposed, book) && project.Rename is null) return book;
        var plan = proposed.Orders.Single(o => o.Id == orderId).Projects.Single(p => p.Id == projectId).Rename!;
        var source = Path.GetDirectoryName(project.NativePath!)!;
        var target = Path.GetDirectoryName(plan.NativePath)!;
        var parent = Path.GetDirectoryName(source)!;
        try
        {
            ValidatePaths(project, plan);
            RejectLinks(parent);
            using var operationLease = new FileStream(Path.Combine(parent, ".workflow-edit-" + project.Id.ToString("N") + ".lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var folder = LocateFolder(source, target, project.Id);
            var actual = LocateNative(folder, project, plan);
            ValidateContents(folder, actual, project.Id);
            // Deny readers/writers while allowing this process to rename the open empty file on Windows.
            using var nativeLease = new FileStream(actual, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
            if (nativeLease.Length != 0) throw Unsafe("Файл проекта уже заполнен. Для переделки создайте другой вариант.", folder);
            token.ThrowIfCancellationRequested();
            if (project.Rename is null)
            {
                book = await store.WriteOrderAsync(orderId, expectedVersion, current =>
                    WorkflowProjects.BeginRename(current, orderId, expectedVersion, projectId, plan), token).ConfigureAwait(false);
                order = book.Orders.Single(o => o.Id == orderId);
                project = order.Projects.Single(p => p.Id == projectId);
                if (project.Rename is not { } savedPlan || !savedPlan.SameOperation(plan)) throw new WorkflowConflictException();
            }
            // Cancellation after the durable intent must not misreport a committed filesystem change.
            EnsureReceipt(folder, project.Id);
            var renamedFile = Path.Combine(folder, Path.GetFileName(plan.NativePath));
            if (!actual.Equals(renamedFile, Comparison)) File.Move(actual, renamedFile, overwrite: false);
            if (!folder.Equals(target, Comparison)) (moveDirectory ?? Directory.Move)(folder, target);
            ValidateContents(target, plan.NativePath, project.Id);
            return await store.WriteOrderAsync(orderId, order.Version, current =>
                WorkflowProjects.CompleteRename(current, orderId, order.Version, projectId, plan.Id), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ProjectFileFailure ||
            project.Rename is not null && (ex is WorkflowConflictException or WorkflowValidationException))
        {
            // A failed workspace commit can have an unknown outcome. Inspect, never replay it blindly.
            try
            {
                var latest = await store.ReadOrderAsync(orderId, CancellationToken.None).ConfigureAwait(false);
                var saved = latest.Orders.Single(o => o.Id == orderId).Projects.Single(p => p.Id == projectId);
                if (saved.Rename is null && saved.NativePath == plan.NativePath && saved.MachineId == plan.MachineId && saved.ThicknessMm == plan.ThicknessMm)
                    return latest;
            }
            catch (Exception readError) when (readError is IOException or UnauthorizedAccessException or WorkflowValidationException) { }
            if (ex is ProjectFileFailure) throw;
            var code = ex.HResult & 0xFFFF;
            throw ex switch
            {
                UnauthorizedAccessException => new ProjectFileFailure(ProjectFileFailureKind.AccessDenied, "Нет прав на изменение папки проекта. Проверьте доступ к папке.", source, ex),
                FileNotFoundException or DirectoryNotFoundException => new ProjectFileFailure(ProjectFileFailureKind.Missing, "Папка или файл проекта не найдены. Проверьте путь и подключение к хранилищу.", source, ex),
                _ when code is 32 or 33 => new ProjectFileFailure(ProjectFileFailureKind.Busy, "Папка проекта или файл в ней заняты другой программой. Закройте открытый проект и связанные файлы, затем нажмите «Повторить».", source, ex),
                WorkflowConflictException or WorkflowValidationException => new ProjectFileFailure(ProjectFileFailureKind.Incomplete, "Файловая операция начата, но запись параметров не завершена. Вернитесь к редактору и продолжите начатое изменение после проверки заявки.", source, ex),
                _ => new ProjectFileFailure(ProjectFileFailureKind.Unavailable, "Не удалось завершить изменение проекта. Проверьте доступ к папке и подключение к хранилищу. Начатое изменение можно продолжить повторной попыткой.", source, ex)
            };
        }
    }

    private static void ValidatePaths(CuttingProject project, ProjectRename plan)
    {
        var native = Path.GetFullPath(project.NativePath!);
        var source = Path.GetDirectoryName(native)!; var target = Path.GetDirectoryName(Path.GetFullPath(plan.NativePath))!;
        if (!Path.GetFileName(source).Equals(project.Name, Comparison) ||
            !Path.GetFileNameWithoutExtension(native).Equals(project.Name, Comparison) ||
            !Path.GetDirectoryName(source)!.Equals(Path.GetDirectoryName(target), Comparison) ||
            !Path.GetFileName(target).Equals(plan.Name, Comparison) ||
            !Path.GetFileNameWithoutExtension(plan.NativePath).Equals(plan.Name, Comparison))
            throw Unsafe("Путь проекта не соответствует созданной заготовке. Автоматическое переименование недоступно.", source);
    }

    private static string LocateFolder(string source, string target, Guid id)
    {
        if (source.Equals(target, Comparison)) return source;
        if (Directory.Exists(source))
        {
            if (Directory.Exists(target) || File.Exists(target))
                throw new ProjectFileFailure(ProjectFileFailureKind.Collision, "Папка с новым именем уже существует. Её содержимое не будет перезаписано.", target);
            return source;
        }
        if (Directory.Exists(target) && HasReceipt(target, id)) return target;
        throw new DirectoryNotFoundException(source);
    }

    private static string LocateNative(string folder, CuttingProject project, ProjectRename plan)
    {
        var original = Path.Combine(folder, Path.GetFileName(project.NativePath!));
        var renamed = Path.Combine(folder, Path.GetFileName(plan.NativePath));
        if (File.Exists(original)) return original;
        if (HasReceipt(folder, project.Id) && File.Exists(renamed)) return renamed;
        throw new FileNotFoundException("Файл заготовки не найден.", original);
    }

    private static void ValidateContents(string folder, string native, Guid id)
    {
        RejectLinks(folder);
        if ((File.GetAttributes(native) & FileAttributes.ReparsePoint) != 0) throw Unsafe("Ссылки вместо файла проекта не переименовываются.", folder);
        var entries = Directory.EnumerateFileSystemEntries(folder).Take(4).ToArray();
        if (entries.Any(path => !path.Equals(native, Comparison) &&
            !(Path.GetFileName(path) == Receipt && HasReceipt(folder, id))))
            throw Unsafe("В папке уже есть рабочие файлы или отчёты. Изменение пустой заготовки недоступно; используйте другой вариант проекта.", folder);
    }

    private static void RejectLinks(string folder)
    {
        for (var current = new DirectoryInfo(folder); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw Unsafe("Автоматическое переименование через ссылки на папки недоступно.", folder);
    }

    private static bool HasReceipt(string folder, Guid id)
    {
        var path = Path.Combine(folder, Receipt);
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        return (info.Attributes & FileAttributes.ReparsePoint) == 0 && info.Length == 32 && File.ReadAllText(path) == id.ToString("N");
    }

    private static void EnsureReceipt(string folder, Guid id)
    {
        if (HasReceipt(folder, id)) return;
        using var file = new FileStream(Path.Combine(folder, Receipt), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(System.Text.Encoding.ASCII.GetBytes(id.ToString("N"))); file.Flush(true);
    }

    private static ProjectFileFailure Unsafe(string message, string path) => new ProjectFileFailure(ProjectFileFailureKind.Unsafe, message, path);
}
