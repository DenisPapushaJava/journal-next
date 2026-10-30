using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowViewModel
{
    private bool _catalogUpdating;
    public string PriceSourceDescription => _workspace.PriceSource is { } source
        ? $"Цены загружены {source.LoadedAt.ToLocalTime():dd.MM.yyyy HH:mm} · {Path.GetFileName(source.Path)}"
        : "База с ценами не подключена";

    public WorkflowDatabaseViewModel CreateDatabaseSettings(ILegacyPriceCatalogReader? reader = null) =>
        new(_workspace.PriceSource?.Path, PriceSourceDescription, reader ?? new LegacyPriceCatalogReader(), ApplyLegacyPricesAsync);

    public WorkflowCatalogViewModel CreateCatalogSettings() => new(() => _workspace, SaveCatalogAsync, MachineRemovalReason);
    private string? MachineRemovalReason(Guid id)
    {
        if (WorkflowChanges.MachineRemovalReason(_workspace, id) is { } saved) return saved;
        var name = _workspace.Machines.SingleOrDefault(m => m.Id == id)?.Name;
        bool Uses(SpecificationDraft draft) => draft.MachineId == id || name is not null && string.Equals(draft.Machine, name, StringComparison.OrdinalIgnoreCase);
        if (Drafts.Any(Uses) || IsPlanning && PlanMachine?.Id == id || _preparedProject?.MachineId == id ||
            _cards.Where(p => p.Key != _openOrderId || IsCreating).Any(p => p.Value.Drafts.Any(Uses) ||
                p.Value.Plan.IsOpen && p.Value.Plan.MachineId == id || p.Value.Plan.Prepared?.MachineId == id))
            return "Станок выбран в открытом черновике. Завершите или отмените подготовку/ввод.";
        return null;
    }
    public WorkflowSettingsViewModel CreateGeneralSettings() => new(_store.FilePath, () => _workspace, SaveGeneralSettingsAsync, CaptureProfile, SaveProfileAsync,
        CaptureEntrySuggestions, SaveEntrySuggestionsAsync, this);
    private async Task<string?> SaveGeneralSettingsAsync(string? expectedRoot, string? root, CancellationToken token)
    {
        if (!CanUse) return "Дождитесь завершения текущей операции.";
        var saved = false;
        await RunAsync(async () =>
        {
            _workspace = await _store.WriteProjectRootAsync(expectedRoot, root, token);
            saved = true; RefreshProjects();
        });
        if (!saved && Error is null) token.ThrowIfCancellationRequested();
        return saved ? null : Error ?? "Настройки не сохранены.";
    }
    private async Task<string?> SaveCatalogAsync(long expectedVersion, WorkflowMachine[] machines, CuttingRate[] rates, CancellationToken token)
    {
        if (!CanUse) return "Дождитесь завершения текущей операции.";
        var saved = false;
        foreach (var removed in _workspace.Machines.Where(m => !machines.Any(next => next.Id == m.Id)))
            if (MachineRemovalReason(removed.Id) is { } reason) return reason;
        await RunAsync(async () =>
        {
            _workspace = await _store.WriteAsync(expectedVersion, book => WorkflowChanges.UpdateCatalog(book, machines, rates), token, backupBeforeWrite: true);
            saved = true; NotifyPriceCatalog(); RefreshProjects();
        });
        if (!saved && Error is null) token.ThrowIfCancellationRequested();
        return saved ? null : Error ?? "Изменения не сохранены.";
    }

    public async Task<string?> ApplyLegacyPricesAsync(string path, LegacyPriceCatalog catalog, CancellationToken token)
    {
        if (!CanUse) return "Дождитесь завершения текущей операции.";
        var saved = false;
        await RunAsync(async () =>
        {
            path = LegacyPriceCatalogReader.NormalizePath(path);
            _workspace = await _store.WriteAsync(_workspace.Version,
                workspace => LegacyPriceImport.Apply(workspace, path, catalog, DateTimeOffset.UtcNow), token);
            saved = true;
            NotifyPriceCatalog();
        });
        if (!saved && Error is null) token.ThrowIfCancellationRequested();
        return saved ? null : Error ?? "Загрузка цен не выполнена.";
    }

    private void NotifyPriceCatalog(bool preserveDrafts = false)
    {
        NotifyProjectMachines();
        OnPropertyChanged(nameof(PriceSourceDescription));
        if (preserveDrafts) return;
        _catalogUpdating = true;
        try { foreach (var draft in Drafts) ConfigurePriceSuggestions(draft); }
        finally { _catalogUpdating = false; }
        // A new empty/manual draft can pick up the imported tariff, but an existing
        // calculation or manually entered price is never silently replaced.
        if (SelectedDraft is { Saved: null, PricePerMeter.Length: 0 } draftToPrice) TryApplyCatalogRate(draftToPrice);
    }

    private void ConfigurePriceSuggestions(SpecificationDraft draft)
    {
        var machine = draft.MachineId != Guid.Empty ? _workspace.Machines.SingleOrDefault(m => m.Id == draft.MachineId) :
            _workspace.Machines.SingleOrDefault(m => m.Name == draft.Machine);
        draft.UseMachine(machine);
        draft.UsesMaterialCategories = machine?.ImportProfile == "hans-pdf/v1";
        if (!draft.UsesMaterialCategories && (draft.Saved is null || draft.MachineId != draft.Saved.Parameters.MachineId))
            draft.MaterialGroup = "Базовая";
        var machines = _workspace.Machines.Select(m => m.Name).Order().ToArray();
        var groups = draft.UsesMaterialCategories ? MaterialGroups.ToArray() : ["Базовая"];
        var thicknesses = _workspace.Rates.Where(r => r.MachineId == draft.MachineId && r.MaterialGroup == draft.MaterialGroup)
            .Select(r => r.ThicknessMm).Distinct().Order().Select(SpecificationDraft.N).ToArray();
        draft.SetPriceSuggestions(machines, groups, thicknesses);
    }

    private void TryApplyCatalogRate(SpecificationDraft draft)
    {
        try
        {
            var thickness = SpecificationDraft.Decimal(draft.Thickness, "толщину");
            var rate = _workspace.Rates.SingleOrDefault(r => r.MachineId == draft.MachineId &&
                r.MaterialGroup == draft.MaterialGroup && r.ThicknessMm == thickness);
            if (rate is not null) draft.ApplyRate(rate);
        }
        catch (WorkflowValidationException) { /* Incomplete input is shown by the existing preview. */ }
    }
}
