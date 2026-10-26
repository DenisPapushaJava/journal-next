using Jornal.Workflow.Core;

namespace Jornal.Workflow.Infrastructure;

public static class WorkflowProjectFiles
{
    public static async Task<CuttingProject> PrepareAsync(string root, WorkflowOrder order, string customer,
        WorkflowMachine machine, decimal thickness, Guid? itemId,
        DateTimeOffset now, CancellationToken token = default)
    {
        if (order.IsDeleted) throw new WorkflowValidationException("Заявка находится в корзине. Восстановите её перед подготовкой проекта.");
        if (order.HasProjectRename) throw new WorkflowValidationException("Сначала завершите изменение параметров проекта.");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new WorkflowValidationException("Сначала выберите папку проектов в настройках.");
        if (thickness <= 0)
            throw new WorkflowValidationException("Укажите положительную толщину проекта.");
        var extension = WorkflowProjectNames.Extension(machine);
        if (itemId is not null && !order.Items.Any(i => i.Id == itemId))
            throw new WorkflowValidationException("Позиция заявки не найдена.");
        var id = Guid.NewGuid(); var item = itemId ?? Guid.NewGuid();
        var variant = order.Projects.Where(p => p.ItemId == item).Select(p => p.Variant).DefaultIfEmpty().Max() + 1;
        // Keep the existing order-folder component when upgrading an older display number.
        var orderFolder = order.Projects.Where(p => p.NativePath is not null)
            .Select(p => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(p.NativePath))))
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ??
            order.DisplayNumber + "-" + order.Id.ToString("N")[..8] + " " + SafeComponent(customer);
        var claims = Path.Combine(root, ".workflow-names"); Directory.CreateDirectory(claims);
        WorkflowServiceFiles.TryHide(claims);
        // CreateNew is the reservation, including across orders/clients. Seconds alone do not guarantee uniqueness.
        for (var offset = 0; offset < 3600; offset++)
        {
            token.ThrowIfCancellationRequested();
            var allocated = now.AddSeconds(offset); var name = WorkflowProjectNames.Name(machine, thickness, allocated);
            var claim = Path.Combine(claims, allocated.ToString("yyyy-MM-dd-HHmmss") + ".claim");
            FileStream lease;
            try { lease = new(claim, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(claim)) { continue; }
            await using (lease)
            {
                var folder = Path.Combine(root, allocated.ToString("yyyy"), allocated.ToString("MM"), allocated.ToString("dd"),
                    orderFolder, name);
                var native = Path.Combine(folder, name + extension);
                // Keep a receipt even if later persistence fails; never reuse a name or delete somebody's working files.
                await using (var writer = new StreamWriter(lease, leaveOpen: true))
                { await writer.WriteAsync((id + "\n" + native).AsMemory(), token); await writer.FlushAsync(token); }
                lease.Flush(true);
                Directory.CreateDirectory(folder);
                using (var file = new FileStream(native, FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Flush(true);
                return new(id, item, variant, machine.Id, thickness, name, native,
                    machine.ImportProfile, allocated);
            }
        }
        throw new WorkflowValidationException("Не удалось подобрать свободное имя проекта. Проверьте папку и повторите.");
    }

    private static string SafeComponent(string value)
    {
        const string forbidden = "<>:\"/\\|?*";
        var safe = new string(value.Select(c => char.IsControl(c) || forbidden.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "Заказчик" : safe[..Math.Min(60, safe.Length)];
    }
}
