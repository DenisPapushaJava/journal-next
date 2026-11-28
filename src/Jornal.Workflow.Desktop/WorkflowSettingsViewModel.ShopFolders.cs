using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowSettingsViewModel
{
    private string? _originalShopRoot;
    private string? _originalOfficeCopiesRoot;
    private string? _originalCalculationExchangeRoot;
    private (bool Combine, bool AsPdf) _originalHansMaps;
    [ObservableProperty] private string _shopRoot = "";
    [ObservableProperty] private string _officeCopiesRoot = "";
    [ObservableProperty] private string _calculationExchangeRoot = "";
    [ObservableProperty] private bool _combineHansMaps = true;
    private bool _hansMapsAsPdf;
    public bool HansMapsPdf
    {
        get => _hansMapsAsPdf;
        set { if (value) SelectHansMapFormat(true); }
    }
    public bool HansMapsDoc
    {
        get => !_hansMapsAsPdf;
        set { if (value) SelectHansMapFormat(false); }
    }
    private void SelectHansMapFormat(bool asPdf)
    {
        if (_hansMapsAsPdf == asPdf) return;
        _hansMapsAsPdf = asPdf;
        OnPropertyChanged(nameof(HansMapsDoc));
        OnPropertyChanged(nameof(HansMapsPdf));
        InputChanged();
    }
    partial void OnCombineHansMapsChanged(bool value) => InputChanged();
    public bool HasShopRootChanges => ShopRoot != (_originalShopRoot ?? "");
    private bool HasOfficeCopiesRootChanges => IsRemoteStorage && OfficeCopiesRoot != (_originalOfficeCopiesRoot ?? "");
    private bool HasCalculationExchangeRootChanges => CalculationExchangeRoot != (_originalCalculationExchangeRoot ?? "");
    private bool HasHansMapChanges => (CombineHansMaps, _hansMapsAsPdf) != _originalHansMaps;
    public string ShopFolderPreview => Journal.ShopFolderPreview(ShopRoot);
    private void ReloadShopRoot()
    {
        _loading = true;
        try { _originalShopRoot = Journal.ShopRoot; ShopRoot = _originalShopRoot ?? ""; }
        finally { _loading = false; }
        Changed();
    }

    private void ReloadOfficeCopiesRoot()
    {
        _loading = true;
        try { _originalOfficeCopiesRoot = Journal.OfficeCopiesRoot; OfficeCopiesRoot = _originalOfficeCopiesRoot ?? ""; }
        finally { _loading = false; }
        Changed();
    }

    partial void OnOfficeCopiesRootChanged(string value) => InputChanged();
    partial void OnCalculationExchangeRootChanged(string value) => InputChanged();

    private void ReloadCalculationExchangeRoot()
    {
        _loading = true;
        try { _originalCalculationExchangeRoot = Journal.CalculationExchangeRoot; CalculationExchangeRoot = _originalCalculationExchangeRoot ?? ""; }
        finally { _loading = false; }
        Changed();
    }

    private void ReloadHansMaps()
    {
        _loading = true;
        try
        {
            _originalHansMaps = Journal.HansMapPreferences;
            CombineHansMaps = _originalHansMaps.Combine;
            SelectHansMapFormat(_originalHansMaps.AsPdf);
        }
        finally { _loading = false; }
        Changed();
    }
    partial void OnShopRootChanged(string value)
    { OnPropertyChanged(nameof(ShopFolderPreview)); InputChanged(); }
    private async Task SaveHansMapsAsync(CancellationToken token)
    {
        Error = await Journal.SaveHansMapPreferencesAsync(_originalHansMaps, (CombineHansMaps, _hansMapsAsPdf), token);
        if (Error is not null) return;
        ReloadHansMaps();
        Status = Status is null ? "Настройки передачи в цех сохранены на этом компьютере." : Status + " Настройки передачи в цех сохранены.";
    }

    private async Task SaveStorageAsync(CancellationToken token)
    {
        // Validate the edits before updating the shared or local store.
        if (HasRootChanges) _ = WorkflowProjects.SetRoot(_snapshot(), ProjectRoot);
        if (HasShopRootChanges) WorkflowShopFolderPaths.NormalizeRoot(ShopRoot);
        string? copiesRoot = null;
        if (HasOfficeCopiesRootChanges && !string.IsNullOrWhiteSpace(OfficeCopiesRoot))
        {
            var input = OfficeCopiesRoot.Trim();
            if (!Path.IsPathFullyQualified(input) || input.Any(char.IsControl))
                throw new WorkflowValidationException("Укажите полный путь к папке для локальных копий или оставьте поле пустым.");
            copiesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
            if (!await Task.Run(() => Directory.Exists(copiesRoot), token))
                throw new WorkflowValidationException("Папка для локальных копий недоступна. Выберите существующую папку.");
        }
        string? exchangeRoot = null;
        if (HasCalculationExchangeRootChanges && !string.IsNullOrWhiteSpace(CalculationExchangeRoot))
        {
            var path = CalculationExchangeRoot.Trim();
            if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
                throw new WorkflowValidationException("Укажите полный путь к папке обмена расчётами или оставьте поле пустым.");
            exchangeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (!await Task.Run(() => Directory.Exists(exchangeRoot), token))
                throw new WorkflowValidationException("Выберите существующую папку обмена расчётами.");
        }
        Status = null;
        if (HasRootChanges)
        {
            Error = await _saveRoot(_expectedRoot, ProjectRoot, token);
            if (Error is not null) return;
            ReloadRoot(); Status = "Общая папка проектов сохранена.";
        }
        if (HasShopRootChanges)
        {
            Error = await Journal.SaveShopRootAsync(_originalShopRoot, ShopRoot, token);
            if (Error is not null) return;
            ReloadShopRoot();
            Status = Status is null ? "Папка цеха сохранена на этом компьютере." : "Общая папка проектов и личный путь цеха сохранены.";
        }
        if (HasOfficeCopiesRootChanges)
        {
            Error = await Journal.SaveOfficeCopiesRootAsync(_originalOfficeCopiesRoot, copiesRoot, token);
            if (Error is not null) return;
            ReloadOfficeCopiesRoot();
            Status = Status is null ? "Папка для локальных копий сохранена." : Status + " Папка для локальных копий сохранена.";
        }
        if (HasCalculationExchangeRootChanges)
        {
            Error = await Journal.SaveCalculationExchangeRootAsync(_originalCalculationExchangeRoot, exchangeRoot, token);
            if (Error is not null) return;
            ReloadCalculationExchangeRoot();
            Status = "Папка обмена расчётами сохранена на этом компьютере.";
        }
    }
}
