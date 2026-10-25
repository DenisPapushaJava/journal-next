namespace Jornal.Workflow.Core;

public static partial class WorkflowChanges
{
    public static WorkflowWorkspace SaveEmployeeProfile(WorkflowWorkspace workspace, Employee? expected, string name, uint color)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 80 || name.Any(char.IsControl))
            throw new WorkflowValidationException("Укажите фамилию технолога — до 80 символов.");
        if (color >> 24 != 255) throw new WorkflowValidationException("Выберите непрозрачный цвет сотрудника.");
        var matches = workspace.Employees.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new WorkflowValidationException("В справочнике несколько сотрудников с такой фамилией. Укажите отличающееся имя.");
        if (matches.Length == 0)
        {
            if (expected is not null) throw new WorkflowConflictException();
            return workspace with { Employees = [.. workspace.Employees, new(Guid.NewGuid(), name) { ColorArgb = color, IsTechnologist = true }] };
        }
        var current = matches[0];
        if (current.CanWork && current.ColorArgb == color && (expected is null || expected.Id == current.Id)) return workspace;
        if (expected is null || expected.Id != current.Id || expected.Name != current.Name ||
            expected.IsTechnologist != current.IsTechnologist || expected.IsTechnologistArchived != current.IsTechnologistArchived ||
            current.ColorArgb != expected.ColorArgb && current.ColorArgb != color)
            throw new WorkflowConflictException();
        return workspace with { Employees = workspace.Employees.Select(e => e.Id == current.Id ?
            e with { ColorArgb = color, IsTechnologist = true, IsTechnologistArchived = false } : e).ToArray() };
    }

    public static WorkflowWorkspace ArchiveTechnologist(WorkflowWorkspace workspace, Employee expected, Guid actorId)
    {
        if (!workspace.Employees.Any(e => e.Id == actorId && e.CanWork))
            throw new WorkflowValidationException("Выберите себя: Настройки → Общие → Технолог.");
        if (expected.Id == actorId)
            throw new WorkflowValidationException("Это текущий технолог на этом компьютере. Сначала выберите другого.");
        var current = workspace.Employees.SingleOrDefault(e => e.Id == expected.Id);
        if (current is null || current != expected) throw new WorkflowConflictException();
        if (!current.IsTechnologist) throw new WorkflowValidationException("Эта запись не является технологом.");
        if (current.IsTechnologistArchived) return workspace;
        return workspace with { Employees = workspace.Employees.Select(e => e.Id == current.Id ? e with { IsTechnologistArchived = true } : e).ToArray() };
    }
    public static WorkflowWorkspace UpdateOrder(WorkflowWorkspace workspace, Guid orderId, long expectedVersion,
        Guid managerId, Guid technologistId, string? blcb, Guid actorId, DateTimeOffset at)
    {
        var order = RequireActiveOrder(workspace, orderId, expectedVersion);
        if (!workspace.Employees.Any(e => e.Id == managerId) ||
            !workspace.Employees.Any(e => e.Id == technologistId && (e.CanWork || order.TechnologistId == technologistId)) ||
            !workspace.Employees.Any(e => e.Id == actorId && e.CanWork)) throw new WorkflowValidationException("Выберите действующего технолога из списка.");
        blcb = string.IsNullOrWhiteSpace(blcb) ? null : blcb.Trim();
        if (blcb?.Length > 255) throw new WorkflowValidationException("Номер БЛЦБ — до 255 символов.");
        if (order.ManagerId == managerId && order.TechnologistId == technologistId && order.Blcb == blcb) return workspace;
        var saved = order with { ManagerId = managerId, TechnologistId = technologistId, Blcb = blcb,
            UpdatedAt = at, Version = order.Version + 1 };
        return workspace with { Orders = workspace.Orders.Select(o => o.Id == order.Id ? saved : o).ToArray() };
    }

    public static WorkflowWorkspace CreateOrder(WorkflowWorkspace workspace, string customerName, Guid managerId, Guid technologistId,
        Guid actorId, DateTimeOffset at, out Guid orderId)
    {
        var name = customerName.Trim();
        if (name.Length is 0 or > 255) throw new WorkflowValidationException("Укажите заказчика.");
        if (!workspace.Employees.Any(e => e.Id == managerId) || !workspace.Employees.Any(e => e.Id == technologistId && e.CanWork) || !workspace.Employees.Any(e => e.Id == actorId && e.CanWork))
            throw new WorkflowValidationException("Выберите менеджера и технолога.");
        var customer = workspace.Customers.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? new(Guid.NewGuid(), name);
        orderId = Guid.NewGuid();
        var order = new WorkflowOrder(orderId, workspace.Orders.Select(o => o.Number).DefaultIfEmpty().Max() + 1,
            customer.Id, managerId, technologistId, null, 1, at, at, [])
        { ReferenceNumber = WorkflowOrderNumbers.Allocate(workspace.Orders.Select(o => o.ReferenceNumber).ToHashSet(StringComparer.Ordinal), at), CreatedById = actorId };
        return workspace with { Customers = workspace.Customers.Any(c => c.Id == customer.Id) ? workspace.Customers : [.. workspace.Customers, customer], Orders = [order, .. workspace.Orders] };
    }

    public static WorkflowWorkspace SetRate(WorkflowWorkspace workspace, CuttingRate rate)
    {
        var machine = ResolveMachine(workspace, rate.MachineId, rate.Machine);
        rate = rate with { MachineId = machine.Id, Machine = machine.Name };
        if (string.IsNullOrWhiteSpace(rate.Machine) || string.IsNullOrWhiteSpace(rate.MaterialGroup) || rate.ThicknessMm <= 0 ||
            rate.PricePerMeter < 0 || rate.PricePerPierce is < 0)
            throw new WorkflowValidationException("Проверьте станок, толщину и тарифы.");
        var previous = workspace.Rates.SingleOrDefault(r => r.MachineId == rate.MachineId && r.MaterialGroup == rate.MaterialGroup && r.ThicknessMm == rate.ThicknessMm);
        if (previous is not null && previous.PricePerMeter == rate.PricePerMeter && previous.PricePerPierce == rate.PricePerPierce) return workspace;
        // Сохранённый расчёт удерживает свой тариф при изменении общего прайса.
        return workspace with { Rates = [.. workspace.Rates.Where(r => r.MachineId != rate.MachineId || r.MaterialGroup != rate.MaterialGroup || r.ThicknessMm != rate.ThicknessMm), rate] };
    }

    public static WorkflowWorkspace SaveSpecification(WorkflowWorkspace workspace, Guid orderId, long expectedOrderVersion,
        Guid specificationId, CuttingParameters parameters, string documentNumber, DateOnly documentDate,
        Guid actorId, DateTimeOffset at, string? sourcePath = null, string? profile = null, CuttingRate? quotedRate = null,
        AcceptedReport? report = null)
    {
        var order = RequireActiveOrder(workspace, orderId, expectedOrderVersion);
        var machine = ResolveMachine(workspace, parameters.MachineId, parameters.Machine);
        parameters = parameters with { MachineId = machine.Id, Machine = machine.Name };
        if (quotedRate is { MachineId: var quotedMachine } && quotedMachine != Guid.Empty && quotedMachine != parameters.MachineId)
            throw new WorkflowValidationException("Тариф принадлежит другому станку.");
        if (quotedRate is { MachineId: var missingMachine } && missingMachine == Guid.Empty) quotedRate = quotedRate with { MachineId = parameters.MachineId };
        if (!workspace.Employees.Any(e => e.Id == actorId && e.CanWork) || string.IsNullOrWhiteSpace(documentNumber) || documentDate == default)
            throw new WorkflowValidationException("Укажите номер, дату спецификации и автора сохранения.");
        var existing = order.Specifications.SingleOrDefault(s => s.Id == specificationId);
        var project = order.Projects.SingleOrDefault(p => p.Id == specificationId);
        if (project?.IsDeleted == true) throw new WorkflowValidationException("Проект находится в корзине. Восстановите его перед изменением расчёта.");
        if (project is not null && (project.MachineId != parameters.MachineId || project.ThicknessMm != parameters.ThicknessMm))
            throw new WorkflowValidationException("Станок и толщина не соответствуют проекту. Создайте другой вариант проекта.");
        report ??= existing?.Current.Report;
        report?.Validate();
        var rate = quotedRate ?? workspace.Rates.SingleOrDefault(r => r.MachineId == parameters.MachineId && r.MaterialGroup == parameters.MaterialGroup && r.ThicknessMm == parameters.ThicknessMm)
            ?? throw new WorkflowValidationException("Для выбранного станка, категории и толщины нет тарифа. Сначала сохраните тариф.");
        if (quotedRate is not null && !workspace.Rates.Any(r => r.Id == quotedRate.Id && r.MachineId == quotedRate.MachineId &&
            r.MaterialGroup == quotedRate.MaterialGroup && r.ThicknessMm == quotedRate.ThicknessMm && r.PricePerMeter == quotedRate.PricePerMeter && r.PricePerPierce == quotedRate.PricePerPierce) && !(existing?.Current is { } r &&
            r.Calculation.RateId == quotedRate.Id && r.Calculation.PricePerMeter == quotedRate.PricePerMeter &&
            r.Calculation.PricePerPierce == quotedRate.PricePerPierce && r.Parameters.MachineId == quotedRate.MachineId &&
            r.Parameters.MaterialGroup == quotedRate.MaterialGroup && r.Parameters.ThicknessMm == quotedRate.ThicknessMm))
            throw new WorkflowValidationException("Тариф не сохранён. Сначала сохраните тариф или обновите его из прайса.");
        var calculation = CuttingCalculation.Calculate(parameters, rate);
        if (existing is not null && existing.Current.Parameters.SameInputs(parameters) && existing.Current.Calculation == calculation &&
            existing.Current.DocumentNumber == documentNumber && existing.Current.DocumentDate == documentDate &&
            existing.Current.SourcePath == sourcePath && existing.Current.ImportProfile == profile &&
            existing.Current.Report == report) return workspace;
        var state = new SpecificationState(documentNumber.Trim(), documentDate,
            at, actorId, parameters, calculation, sourcePath, profile) { Report = report };
        var saved = new WorkflowSpecification(specificationId, state);
        var specifications = existing is null ? [.. order.Specifications, saved] : order.Specifications.Select(s => s.Id == saved.Id ? saved : s).ToArray();
        var next = order with { Specifications = specifications, Version = order.Version + 1, UpdatedAt = at };
        return workspace with { Orders = workspace.Orders.Select(o => o.Id == next.Id ? next : o).ToArray() };
    }

    public static WorkflowMachine ResolveMachine(WorkflowWorkspace workspace, Guid id, string name) =>
        (id != Guid.Empty ? workspace.Machines.SingleOrDefault(m => m.Id == id) :
            workspace.Machines.SingleOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        ?? throw new WorkflowValidationException("Выберите станок из справочника «Станки и цены» или добавьте новый.");

    public static WorkflowWorkspace UpdateCatalog(WorkflowWorkspace workspace, IReadOnlyList<WorkflowMachine> machines, IReadOnlyList<CuttingRate> rates)
    {
        if (machines.Count > 256 || machines.Any(m => m.Id == Guid.Empty || string.IsNullOrWhiteSpace(m.Name) || m.Name != m.Name.Trim() ||
            m.Name.Length > 255 || m.Name.Any(char.IsControl) || !WorkflowMachine.SupportsProfile(m.ImportProfile)) ||
            machines.Select(m => m.Id).Distinct().Count() != machines.Count ||
            machines.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != machines.Count)
            throw new WorkflowValidationException("Проверьте названия станков: они должны быть уникальными и непустыми.");
        foreach (var removed in workspace.Machines.Where(m => !machines.Any(next => next.Id == m.Id)))
            if (MachineRemovalReason(workspace, removed.Id) is { } reason)
                throw new WorkflowValidationException(removed.Name + ": " + reason);
        foreach (var machine in machines) WorkflowProjectNames.Extension(machine);
        foreach (var project in workspace.Orders.SelectMany(o => o.Projects).Where(p => p.Rename is not null))
            foreach (var original in workspace.Machines.Where(m => m.Id == project.MachineId || m.Id == project.Rename!.MachineId))
            {
                var nextMachine = machines.Single(m => m.Id == original.Id);
                if (original.ImportProfile != nextMachine.ImportProfile || original.ProjectExtension != nextMachine.ProjectExtension)
                    throw new WorkflowValidationException("Сначала завершите изменение проекта, затем меняйте формат импорта или расширение станка.");
            }
        var normalized = rates.Select(rate =>
        {
            var machine = machines.SingleOrDefault(m => m.Id == rate.MachineId) ?? throw new WorkflowValidationException("У тарифа не найден станок.");
            if (rate.ThicknessMm <= 0 || rate.PricePerMeter < 0 || rate.PricePerPierce is null or < 0 || string.IsNullOrWhiteSpace(rate.MaterialGroup))
                throw new WorkflowValidationException($"{machine.Name}: проверьте толщину и цены. Цена пробивки должна быть задана явно.");
            var previous = workspace.Rates.SingleOrDefault(r => r.MachineId == rate.MachineId && r.MaterialGroup == rate.MaterialGroup && r.ThicknessMm == rate.ThicknessMm);
            return rate with { Machine = machine.Name, Id = previous is not null && previous.PricePerMeter == rate.PricePerMeter &&
                previous.PricePerPierce == rate.PricePerPierce ? previous.Id : Guid.NewGuid() };
        }).ToArray();
        if (normalized.Select(r => (r.MachineId, r.MaterialGroup, r.ThicknessMm)).Distinct().Count() != normalized.Length)
            throw new WorkflowValidationException("Для одного станка и категории материала толщина не должна повторяться.");
        // Names are mutable labels; machine IDs and saved calculation snapshots remain intact.
        if (workspace.Machines.SequenceEqual(machines) && workspace.Rates.OrderBy(r => r.Id).SequenceEqual(normalized.OrderBy(r => r.Id))) return workspace;
        var next = workspace with { Machines = machines.ToArray(), Rates = normalized }; next.Validate(); return next;
    }

    public static string? MachineRemovalReason(WorkflowWorkspace workspace, Guid machineId) =>
        workspace.Orders.Any(o => o.Projects.Any(p => p.MachineId == machineId || p.Rename?.MachineId == machineId)) ?
            "Есть проекты на этом станке. Удаление недоступно." :
        workspace.Orders.Any(o => o.Specifications.Any(s => s.Current.Parameters.MachineId == machineId)) ?
            "Есть сохранённые расчёты на этом станке. Удаление недоступно." : null;
}
