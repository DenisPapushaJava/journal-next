using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public sealed record CalculationHistoryField(string Field, string Before, string After);
public sealed class CalculationHistoryChange
{
    internal long Sequence { get; }
    internal DateTimeOffset At { get; }
    public string Date { get; }
    public string Caption { get; }
    public string Author { get; }
    public string CostChange { get; }
    public CalculationHistoryField[] Fields { get; }
    public AcceptedReport? Report { get; }
    public bool CanOpenReport => Report is not null;
    public string ReportHint => CanOpenReport ? "Открыть сохранённую спецификацию этого состояния" : "Для этого состояния копия спецификации не сохранялась";
    internal CalculationHistoryChange(CalculationHistorySnapshot before, SpecificationState after, IReadOnlyList<Employee> employees)
    {
        var draft = before.Draft;
        var old = draft is null ? Display(before.State!) : Display(draft);
        var next = Display(after);
        Fields = old.Where(p => p.Value != next[p.Key]).Select(p => new CalculationHistoryField(p.Key, p.Value.Length == 0 ? "—" : p.Value,
            next[p.Key].Length == 0 ? "—" : next[p.Key])).ToArray();
        Report = draft?.Report ?? before.State?.Report;
        if (Report?.Sha256 != after.Report?.Sha256)
            Fields = [..Fields, new("Спецификация", Report?.FileName ?? "Не сохранялась", after.Report is null ? "Не сохранялась" : Report?.FileName == after.Report.FileName ? "Обновлена" : after.Report.FileName + " · обновлена")];
        Sequence = before.OrderVersion; At = draft is null ? after.SavedAt : before.CapturedAt;
        Date = At.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.GetCultureInfo("ru-RU"));
        Caption = draft is null ? "Пересчёт" : "Ручные правки перед обновлением";
        var actor = draft is null ? after.AuthorId : before.CapturedBy;
        Author = employees.SingleOrDefault(e => e.Id == actor)?.Name ?? "Технолог";
        CostChange = Cost(draft?.Calculation ?? before.State?.Calculation) + " → " + Cost(after.Calculation);
    }
    private static string Cost(CuttingCalculation? quote) => quote is null ? "Не рассчитано" : SpecificationDraft.Money(quote.TotalCost) + " ₽";
    internal static string ComparisonKey(SpecificationState state) => JsonSerializer.Serialize(new { Fields = Display(state), Report = state.Report?.Sha256 });
    internal static string ComparisonKey(CalculationDraftSnapshot draft) => JsonSerializer.Serialize(new { Fields = Display(draft), Report = draft.Report?.Sha256 });
    private static Dictionary<string, string> Display(SpecificationState state) => Display(new CalculationDraftSnapshot(
        new(state.DocumentNumber, state.DocumentDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), state.Parameters.Material,
            state.Parameters.MaterialGroup, SpecificationDraft.N(state.Parameters.ThicknessMm), state.Parameters.BlankSize,
            SpecificationDraft.N(state.Parameters.BlankQuantity), SpecificationDraft.N(state.Parameters.CutLengthMeters),
            state.Parameters.PierceCount?.ToString(CultureInfo.InvariantCulture) ?? "", state.Parameters.WeightKg is { } w ? SpecificationDraft.N(w) : "",
            state.Parameters.DurationSeconds is { } t ? SpecificationDraft.FormatDuration(t) : ""),
        SpecificationDraft.N(state.Parameters.ManualCoefficient), state.Parameters.HasBending, state.Parameters.HasChamfer, state.Parameters.Comment,
        SpecificationDraft.N(state.Calculation.PricePerMeter), SpecificationDraft.N(state.Calculation.PricePerPierce), state.Calculation, state.Report));
    private static Dictionary<string, string> Display(CalculationDraftSnapshot draft)
    {
        var v = draft.Values;
        static string Number(string text) => decimal.TryParse(text?.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var n) ? SpecificationDraft.N(n) : text ?? "";
        return new()
        {
            ["Резка"] = Cost(draft.Calculation), ["Материал"] = v.Material ?? "", ["Толщина, мм"] = Number(v.Thickness),
            ["Категория тарифа"] = v.MaterialGroup ?? "", ["Заготовка"] = v.BlankSize ?? "", ["Количество"] = Number(v.Quantity),
            ["Длина реза, м"] = Number(v.Length), ["Пробивки"] = Number(v.Pierces), ["Вес, кг"] = Number(v.Weight), ["Время резки"] = v.Duration ?? "",
            ["Коэффициент"] = Number(draft.Coefficient), ["Цена метра, ₽"] = Number(draft.PricePerMeter), ["Цена пробивки, ₽"] = Number(draft.PricePerPierce),
            ["Гибка"] = draft.HasBending ? "Да" : "Нет", ["Фаска"] = draft.HasChamfer ? "Да" : "Нет", ["Комментарий"] = draft.Comment ?? "",
            ["Номер спецификации"] = v.DocumentNumber ?? "", ["Дата спецификации"] = v.DocumentDate
        };
    }
}

public partial class WorkflowCalculationHistoryViewModel : ObservableObject
{
    public string ProjectName { get; }
    public string? Warning { get; }
    public CalculationHistoryChange[] Changes { get; }
    public bool IsEmpty => Changes.Length == 0;
    public string Summary => IsEmpty ? "Предыдущих расчётов пока нет. Они сохраняются автоматически при обновлении." :
        $"Изменений: {Changes.Length}. Выберите запись, чтобы увидеть «было → стало».";
    [ObservableProperty] private CalculationHistoryChange? _selected;
    [ObservableProperty] private string? _error;
    public WorkflowCalculationHistoryViewModel(string projectName, SpecificationState current, CalculationHistoryRead history, IReadOnlyList<Employee> employees, long currentVersion = long.MaxValue)
    {
        history = history with { Snapshots = history.Snapshots.Where(s => s.OrderVersion <= currentVersion).ToArray() };
        ProjectName = projectName; Warning = history.Warning;
        static string Key(SpecificationState state) => JsonSerializer.Serialize(state);
        var currentKey = Key(current);
        var states = history.Snapshots.Where(s => s.State is not null && s.Draft is null && Key(s.State) != currentKey)
            .GroupBy(s => Key(s.State!)).Select(g => g.First()).OrderBy(s => s.OrderVersion).ThenBy(s => s.State!.SavedAt).ToArray();
        var changes = new List<CalculationHistoryChange>();
        for (var i = 0; i < states.Length; i++) changes.Add(new(states[i], i + 1 < states.Length ? states[i + 1].State! : current, employees));
        var accepted = states.Select(s => CalculationHistoryChange.ComparisonKey(s.State!)).Append(CalculationHistoryChange.ComparisonKey(current)).ToHashSet();
        changes.AddRange(history.Snapshots.Where(s => s.Draft is not null && !accepted.Contains(CalculationHistoryChange.ComparisonKey(s.Draft))).Select(s => new CalculationHistoryChange(s, current, employees)).Where(c => c.Fields.Length > 0));
        Changes = changes.OrderByDescending(c => c.Sequence).ThenByDescending(c => c.At).ToArray(); Selected = Changes.FirstOrDefault();
    }
}
