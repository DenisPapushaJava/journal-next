using Jornal.Workflow.Core;
using System.Globalization;
using System.Text.Json;

namespace Jornal.Workflow.Infrastructure;

public static class WorkflowProjectFiles
{
    private static readonly SemaphoreSlim RemotePreparation = new(1, 1);
    private sealed record RemotePreparationReceipt(Guid OrderId, CuttingProject Project);

    public static Task<CuttingProject> PrepareAsync(string root, WorkflowOrder order, string customer,
        WorkflowMachine machine, decimal thickness, Guid? itemId,
        DateTimeOffset now, CancellationToken token = default) =>
        PrepareAsync(root, order, customer, machine, thickness, itemId, now, Guid.NewGuid(), null, token);

    public static async Task<CuttingProject> PrepareRemoteAsync(string root, WorkflowOrder order, string customer,
        WorkflowMachine machine, decimal thickness, Guid? itemId, Guid projectId, DateTimeOffset now, CancellationToken token)
    {
        if (projectId == Guid.Empty) throw new WorkflowValidationException("Сначала откройте добавление проекта.");
        await RemotePreparation.WaitAsync(token);
        try
        {
            var receipt = Path.Combine(root, ".workflow-names", "remote-" + projectId.ToString("N") + ".json");
            ShopFileSystem.RequireOrdinaryPath(receipt);
            if (!File.Exists(receipt))
                return await PrepareAsync(root, order, customer, machine, thickness, itemId, now, projectId, receipt, token);

            RemotePreparationReceipt? previous;
            await using (var input = File.OpenRead(receipt))
                previous = await JsonSerializer.DeserializeAsync<RemotePreparationReceipt>(input, cancellationToken: token);
            if (previous is not { Project: { NativePath: { } native } project } || previous.OrderId != order.Id ||
                project.Id != projectId || project.MachineId != machine.Id || project.ThicknessMm != thickness ||
                itemId is { } item && project.ItemId != item || WorkflowOfficeProjectPaths.InRoot(root, native) is null)
                throw new WorkflowValidationException("Заготовка уже подготовлена с другими параметрами. Проверьте выбранный проект.");
            ShopFileSystem.RequireOrdinaryPath(native);
            if (!File.Exists(native)) CreateBlank(native);
            return project;
        }
        finally { RemotePreparation.Release(); }
    }

    private static async Task<CuttingProject> PrepareAsync(string root, WorkflowOrder order, string customer,
        WorkflowMachine machine, decimal thickness, Guid? itemId, DateTimeOffset now, Guid id, string? remoteReceipt, CancellationToken token)
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
        var item = itemId ?? Guid.NewGuid();
        var variant = order.Projects.Where(p => p.ItemId == item).Select(p => p.Variant).DefaultIfEmpty().Max() + 1;
        var location = OrderLocation(root, order, customer);
        if (remoteReceipt is not null && WorkflowOfficeProjectPaths.InRoot(root, Path.Combine(location.Folder, "проект", "файл")) is null)
            throw new WorkflowValidationException("Папка заявки находится вне рабочей папки сервера.");
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
                var project = new CuttingProject(id, item, variant, machine.Id, thickness, name, native, machine.ImportProfile, allocated);
                if (remoteReceipt is not null)
                {
                    ShopFileSystem.RequireOrdinaryPath(native);
                    if (Path.Exists(native)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(remoteReceipt)!);
                    await WriteRemoteReceiptAsync(remoteReceipt, new(order.Id, project), token);
                }
                CreateBlank(native);
                return project;
            }
        }
        throw new WorkflowValidationException("Не удалось подобрать свободное имя проекта. Проверьте папку и повторите.");
    }

    private static void CreateBlank(string native)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        using var file = new FileStream(native, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Flush(true);
    }

    private static async Task WriteRemoteReceiptAsync(string path, RemotePreparationReceipt receipt, CancellationToken token)
    {
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = File.Open(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, receipt, cancellationToken: token);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(staging, path, overwrite: false);
        }
        finally { File.Delete(staging); }
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
