using Jornal.Workflow.Core;
using System.Globalization;

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
        var location = OrderLocation(root, order, customer);
        var claims = Path.Combine(location.Root, ".workflow-names"); Directory.CreateDirectory(claims);
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
                var folder = Path.Combine(location.Folder, name);
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

    private static (string Root, string Folder) OrderLocation(string root, WorkflowOrder order, string customer)
    {
        // Persisted paths also survive deletion, customer changes and older display-number migrations.
        // Pick deterministically even when an older version scattered projects across several days.
        var first = order.Projects.Where(p => p.NativePath is not null).OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).FirstOrDefault();
        if (first is not null)
        {
            if (!Path.IsPathFullyQualified(first.NativePath!))
                throw new WorkflowValidationException("Путь существующего проекта неполный. Проверьте папку заявки.");
            var folder = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(first.NativePath!))!);
            // Existing projects use root/year/month/day/order/project/file. Keep their reservation root too.
            // Otherwise changing the setting would lose the reservations for these working names.
            var day = Path.GetDirectoryName(folder);
            var month = Path.GetDirectoryName(day);
            var year = Path.GetDirectoryName(month);
            var previousRoot = Path.GetDirectoryName(year);
            var date = Path.GetFileName(year) + "-" + Path.GetFileName(month) + "-" + Path.GetFileName(day);
            if (folder is null || previousRoot is null || !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new WorkflowValidationException("Не удалось определить папку заявки по пути существующего проекта.");
            return (previousRoot, folder);
        }
        if (!WorkflowOrderNumbers.IsValid(order.ReferenceNumber))
            throw new WorkflowValidationException("Не удалось определить дату заявки. Обновите журнал (F5).");
        // The immutable number retains the creation day's business date after a UTC roundtrip or timezone change.
        var number = order.ReferenceNumber;
        var name = number + "-" + order.Id.ToString("N")[..8] + " " + SafeComponent(customer);
        return (root, Path.Combine(root, number[..4], number.Substring(5, 2), number.Substring(8, 2), name));
    }

    private static string SafeComponent(string value)
    {
        const string forbidden = "<>:\"/\\|?*";
        var safe = new string(value.Select(c => char.IsControl(c) || forbidden.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return safe.Length == 0 ? "Заказчик" : safe[..Math.Min(60, safe.Length)];
    }
}
