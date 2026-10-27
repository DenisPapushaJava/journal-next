using System.Text.Json.Serialization;

namespace Jornal.Workflow.Core;

public sealed record Employee(Guid Id, string Name)
{
    public uint? ColorArgb { get; init; }
    public bool IsTechnologist { get; init; }
    public bool IsTechnologistArchived { get; init; }
    [JsonIgnore] public bool CanWork => IsTechnologist && !IsTechnologistArchived;
}
public sealed record Customer(Guid Id, string Name);
public sealed record CuttingRate(Guid Id, string Machine, string MaterialGroup, decimal ThicknessMm,
    decimal PricePerMeter, decimal? PricePerPierce)
{
    public Guid MachineId { get; init; }
}

public sealed record CuttingParameters(string Machine, string Material, string MaterialGroup, decimal ThicknessMm,
    string BlankSize, decimal BlankQuantity, decimal CutLengthMeters, int? PierceCount, decimal? WeightKg,
    long? DurationSeconds, decimal ManualCoefficient, bool HasBending, string? Comment,
    bool MaterialNeedsReview = false)
{
    public Guid MachineId { get; init; }
    public bool HasChamfer { get; init; }
    public bool SameInputs(CuttingParameters other) => MachineId == other.MachineId && this with { Machine = other.Machine } == other;
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Machine) || string.IsNullOrWhiteSpace(Material) || string.IsNullOrWhiteSpace(MaterialGroup) ||
            ThicknessMm <= 0 || BlankQuantity <= 0 || CutLengthMeters < 0 || PierceCount is < 0 ||
            WeightKg is < 0 || DurationSeconds is < 0 || ManualCoefficient <= 0)
            throw new WorkflowValidationException("Проверьте материал, толщину, количество и параметры резки.");
        if (MaterialNeedsReview) throw new WorkflowValidationException("Подтвердите или исправьте материал спецификации.");
        if (Comment?.Length > 255) throw new WorkflowValidationException("Комментарий — не более 255 символов.");
    }
}

public sealed record CuttingCalculation(Guid RateId, decimal PricePerMeter, decimal PricePerPierce,
    decimal LengthCost, decimal PierceCost, decimal TotalCost, string FormulaVersion = "length-pierces-manual/v1-exact")
{
    public static CuttingCalculation Calculate(CuttingParameters parameters, CuttingRate rate)
    {
        parameters.Validate();
        if ((rate.MachineId != Guid.Empty && parameters.MachineId != Guid.Empty ? rate.MachineId != parameters.MachineId : rate.Machine != parameters.Machine) ||
            rate.MaterialGroup != parameters.MaterialGroup || rate.ThicknessMm != parameters.ThicknessMm)
            throw new WorkflowValidationException("Тариф не соответствует станку, категории материала или толщине.");
        if (rate.PricePerMeter < 0 || rate.PricePerPierce is < 0) throw new WorkflowValidationException("Тариф не может быть отрицательным.");
        if (rate.PricePerPierce is null) throw new WorkflowValidationException("Не задана цена пробивки. Укажите 0, если пробивки отдельно не оплачиваются.");
        if (parameters.PierceCount is null) throw new WorkflowValidationException("Укажите количество пробивок.");
        try
        {
            var length = checked(parameters.CutLengthMeters * rate.PricePerMeter);
            var pierces = checked(parameters.PierceCount.Value * rate.PricePerPierce.Value);
            return new(rate.Id, rate.PricePerMeter, rate.PricePerPierce.Value, length, pierces,
                checked((length + pierces) * parameters.ManualCoefficient));
        }
        catch (OverflowException) { throw new WorkflowValidationException("Значения слишком велики для расчёта."); }
    }
}

public sealed record SpecificationState(string DocumentNumber, DateOnly DocumentDate,
    DateTimeOffset SavedAt, Guid AuthorId, CuttingParameters Parameters, CuttingCalculation Calculation,
    string? SourcePath, string? ImportProfile)
{
    public AcceptedReport? Report { get; init; }
}
public sealed record WorkflowSpecification(Guid Id, SpecificationState Current);
public sealed record WorkflowOrder(Guid Id, long Number, Guid CustomerId, Guid ManagerId, Guid TechnologistId,
    string? Blcb, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    WorkflowSpecification[] Specifications)
{
    public WorkflowOrderItem[] Items { get; init; } = [];
    public CuttingProject[] Projects { get; init; } = [];
    public string ReferenceNumber { get; init; } = "";
    public Guid? CreatedById { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public Guid? DeletedBy { get; init; }
    public WorkflowShopHandoff? ShopHandoff { get; init; }
    [JsonIgnore] public bool HasCurrentShopHandoff => !IsDeleted && ShopHandoff?.OrderVersion == Version;
    [JsonIgnore] public bool IsDeleted => DeletedAt is not null;
    [JsonIgnore] public IEnumerable<CuttingProject> ActiveProjects => Projects.Where(p => !p.IsDeleted);
    [JsonIgnore] public bool HasProjectRename => Projects.Any(p => p.Rename is not null);
    [JsonIgnore] public string DisplayNumber => ReferenceNumber;
    [JsonIgnore] public IEnumerable<WorkflowSpecification> ActiveSpecifications
    {
        get
        {
            var projectIds = Projects.Select(p => p.Id).ToHashSet();
            var currentIds = ActiveProjects.Where(p => Items.Any(i => i.IsIncluded && i.CurrentProjectId == p.Id)).Select(p => p.Id).ToHashSet();
            return Specifications.Where(s => !projectIds.Contains(s.Id) || currentIds.Contains(s.Id));
        }
    }
    [JsonIgnore] public int PendingProjectCount => Items.Count(i => i.IsIncluded && ActiveProjects.Any(p => p.ItemId == i.Id) &&
        (!ActiveProjects.Any(p => p.Id == i.CurrentProjectId) || !Specifications.Any(s => s.Id == i.CurrentProjectId)));
    [JsonIgnore] public decimal CuttingTotal => ActiveSpecifications.Sum(spec => spec.Current.Calculation.TotalCost);
}

public sealed record WorkflowWorkspace(int SchemaVersion, long Version, Employee[] Employees, Customer[] Customers,
    CuttingRate[] Rates, WorkflowOrder[] Orders)
{
    public WorkflowMachine[] Machines { get; init; } = [];
    public WorkflowPriceSource? PriceSource { get; init; }
    public string? ProjectRoot { get; init; }
    public const int CurrentSchema = 12;
    public static WorkflowWorkspace Empty => new(CurrentSchema, 0, [], [], [], []) { Machines = WorkflowMachine.Defaults() };

    public void Validate()
    {
        if (SchemaVersion != CurrentSchema || Version < 0 || Employees is null || Customers is null || Rates is null || Orders is null)
            throw new WorkflowValidationException("Неизвестная структура локальных данных нового Journal.");
        if (Employees.Any(e => e.ColorArgb is { } color && color >> 24 != 255))
            throw new WorkflowValidationException("Цвет сотрудника должен быть непрозрачным.");
        if (Orders.Any(o => !WorkflowOrderNumbers.IsValid(o.ReferenceNumber)) ||
            Orders.Select(o => o.ReferenceNumber).Distinct(StringComparer.Ordinal).Count() != Orders.Length)
            throw new WorkflowValidationException("Номера заявок повреждены или повторяются.");
        if (Machines is null || Machines.Any(m => m is null || m.Id == Guid.Empty || string.IsNullOrWhiteSpace(m.Name) ||
                m.Name.Length > 255 || m.Name.Any(char.IsControl) || !WorkflowMachine.SupportsProfile(m.ImportProfile)) ||
            Machines.Select(m => m.Id).Distinct().Count() != Machines.Length ||
            Machines.Where(m => m.LegacyKey is not null).Select(m => m.LegacyKey).Distinct(StringComparer.Ordinal).Count() != Machines.Count(m => m.LegacyKey is not null) ||
            Machines.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Machines.Length ||
            Rates.Any(r => r is null || r.ThicknessMm <= 0 || r.PricePerMeter < 0 || r.PricePerPierce is < 0 || !Machines.Any(m => m.Id == r.MachineId)) ||
            Rates.Select(r => (r.MachineId, r.MaterialGroup, r.ThicknessMm)).Distinct().Count() != Rates.Length ||
            PriceSource is { } source && (string.IsNullOrWhiteSpace(source.Path) || source.ImportedKeys is null))
            throw new WorkflowValidationException("Каталог станков или тарифов нового журнала повреждён.");
        if (Employees.Select(e => e.Id).Distinct().Count() != Employees.Length ||
            Customers.Select(c => c.Id).Distinct().Count() != Customers.Length || Orders.Select(o => o.Id).Distinct().Count() != Orders.Length)
            throw new WorkflowValidationException("В локальных данных обнаружены повторные идентификаторы.");
        foreach (var machine in Machines) WorkflowProjectNames.Extension(machine);
        foreach (var order in Orders)
        {
            if (order.ShopHandoff is { } handoff && (handoff.OrderVersion < 1 || handoff.OrderVersion > order.Version ||
                handoff.At == default || !Employees.Any(e => e.Id == handoff.ActorId)))
                throw new WorkflowValidationException("Повреждена отметка передачи заявки в цех.");
            if (order.CreatedById is { } creator && !Employees.Any(e => e.Id == creator))
                throw new WorkflowValidationException("Нарушена связь с автором создания заявки.");
            if (order.DeletedAt.HasValue != order.DeletedBy.HasValue || order.DeletedBy is { } deletedBy && !Employees.Any(e => e.Id == deletedBy))
                throw new WorkflowValidationException("Нарушены данные корзины заявки.");
            WorkflowProjects.Validate(order, Machines);
            if (order.Projects.Any(p => p.DeletedBy is { } by && !Employees.Any(e => e.Id == by) ||
                p.Rename is { } rename && !Employees.Any(e => e.Id == rename.ActorId)))
                throw new WorkflowValidationException("Нарушены связи проекта с сотрудниками.");
            if (order.Specifications is null || !Customers.Any(c => c.Id == order.CustomerId) ||
                !Employees.Any(e => e.Id == order.ManagerId) || !Employees.Any(e => e.Id == order.TechnologistId))
                throw new WorkflowValidationException("Нарушены связи заявки со справочниками.");
            foreach (var specification in order.Specifications)
            {
                if (specification.Current is not { } saved || !Employees.Any(e => e.Id == saved.AuthorId) ||
                    !Machines.Any(m => m.Id == saved.Parameters.MachineId))
                    throw new WorkflowValidationException("Нарушены данные сохранённого расчёта спецификации.");
                saved.Parameters.Validate();
                if (saved.Report is { } report) report.Validate();
            }
        }
    }
}

public sealed class WorkflowValidationException(string message) : Exception(message);
public sealed class WorkflowConflictException() : Exception("Данные изменились в другом окне. Обновите список; введённые параметры остались в форме.");
