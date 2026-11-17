using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public sealed record WorkflowShopFolderPath(Guid ProjectId, string ProjectName, string Path);

// Pure path planning: typing a root or viewing settings must not probe a network/cloud folder.
public static class WorkflowShopFolderPaths
{
    public static WorkflowShopFolderPath ForProject(string root, WorkflowOrder order, Guid projectId,
        IReadOnlyList<Employee> employees, IReadOnlyList<Customer> customers, IReadOnlyList<WorkflowMachine> machines)
    {
        var project = order.ActiveProjects.SingleOrDefault(p => p.Id == projectId) ?? throw new WorkflowValidationException("Проект недоступен.");
        var item = order.Items.Single(i => i.Id == project.ItemId);
        var path = Plan(root, order with { Projects = [project], Items = [item with { IsIncluded = true, CurrentProjectId = project.Id }] }, employees, customers, machines).Single();
        if (project.NativePath is { } native && Path.IsPathFullyQualified(native) && Path.GetDirectoryName(native) is { } source &&
            (ShopFileSystem.Under(path.Path, source) || ShopFileSystem.Under(source, path.Path)))
            throw new WorkflowValidationException("Папки проекта и задания в цех пересекаются. Проверьте путь в настройках.");
        return path;
    }

    public static string? NormalizeRoot(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (!System.IO.Path.IsPathFullyQualified(value) || value.Any(char.IsControl))
            throw new WorkflowValidationException("Укажите полный путь к папке заданий в цех или оставьте поле пустым.");
        var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(value));
        if (root.Length > 32760) throw new WorkflowValidationException("Начальный путь слишком длинный. Выберите другую папку; имена проектов не сокращаются.");
        if (OperatingSystem.IsWindows())
        {
            var prefix = System.IO.Path.GetPathRoot(root)!;
            foreach (var part in root[prefix.Length..].Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
                if (!WorkflowWindowsFolderName.IsValid(part)) throw new WorkflowValidationException("Начальный путь содержит недопустимое имя папки Windows. Проверьте путь; имя проекта не меняется.");
        }
        return root;
    }

    public static WorkflowShopFolderPath[] Plan(string root, WorkflowOrder order,
        IReadOnlyList<Employee> employees, IReadOnlyList<Customer> customers, IReadOnlyList<WorkflowMachine> machines)
    {
        root = NormalizeRoot(root) ?? throw new WorkflowValidationException("Выберите папку заданий в цех.");
        if (order.IsDeleted) throw new WorkflowValidationException("Сначала восстановите заявку из корзины.");
        if (order.HasProjectRename) throw new WorkflowValidationException("Завершите изменение параметров проекта.");
        if (string.IsNullOrWhiteSpace(order.Blcb)) throw new WorkflowValidationException("Для предпросмотра укажите и сохраните БЛЦБ заявки.");
        var manager = Component(employees.SingleOrDefault(e => e.Id == order.ManagerId)?.Name, "менеджера");
        var customer = Component(customers.SingleOrDefault(c => c.Id == order.CustomerId)?.Name, "заказчика");
        var blcb = order.Blcb.Trim();
        var blcbLabel = blcb.StartsWith("БЛЦБ-", StringComparison.OrdinalIgnoreCase) ? blcb : "БЛЦБ-" + blcb;
        var orderName = Component(blcbLabel + " " + customer, "БЛЦБ и заказчика");
        var projectSuffix = " " + Component(blcbLabel, "БЛЦБ");
        var active = order.ActiveProjects.ToDictionary(p => p.Id);
        var rows = new List<WorkflowShopFolderPath>();
        foreach (var item in order.Items.Where(i => i.IsIncluded).OrderBy(i => i.Number))
        {
            if (!active.TryGetValue(item.CurrentProjectId, out var project)) continue;
            // Never normalize, abbreviate or regenerate an established project name.
            RequireProjectName(project.Name);
            var folderName = project.Name + projectSuffix;
            if (folderName.Length > 255)
                throw new WorkflowValidationException("Имя папки задания вместе с БЛЦБ превышает 255 символов. Сократите имя проекта или БЛЦБ; автоматического сокращения нет.");
            var machine = machines.SingleOrDefault(m => m.Id == project.MachineId);
            machine?.ValidateShopFolderName();
            var machineFolder = machine?.ShopFolderName ?? Component(machine?.Name, "станка");
            var path = System.IO.Path.Combine(root, machineFolder, manager, orderName, folderName);
            if (path.Length > 32760) throw new WorkflowValidationException("Путь задания слишком длинный. Выберите более короткую начальную папку; имя проекта сохраняется целиком.");
            rows.Add(new(project.Id, project.Name, path));
        }
        if (rows.Count == 0) throw new WorkflowValidationException("Для предпросмотра добавьте или включите проект в состав заявки.");
        if (rows.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
            throw new WorkflowValidationException("У двух проектов совпадает путь задания. Имена не сокращены; проверьте проекты перед созданием папок.");
        return rows.ToArray();
    }

    private static string Component(string? value, string field)
    {
        var result = new string((value ?? "").Select(c => WorkflowWindowsFolderName.IsInvalidCharacter(c) ? '_' : c).ToArray()).Trim().TrimEnd(' ', '.');
        if (result.Length == 0 || result is "." or "..")
            throw new WorkflowValidationException($"Проверьте имя {field} для папки задания.");
        if (WorkflowWindowsFolderName.IsReserved(result)) result = "_" + result;
        if (result.Length > 255) throw new WorkflowValidationException($"Имя {field} слишком длинное для папки. Сокращения автоматически не выполняются.");
        return result;
    }
    private static void RequireProjectName(string name)
    {
        if (!WorkflowWindowsFolderName.IsValid(name))
            throw new WorkflowValidationException("Имя проекта недопустимо для папки Windows. Оно не будет автоматически изменено или сокращено.");
    }
}
