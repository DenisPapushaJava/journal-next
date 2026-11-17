using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowSettingsViewModel
{
    private string? _originalShopRoot;
    private (bool Combine, bool AsPdf) _originalHansMaps;
    [ObservableProperty] private string _shopRoot = "";
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
    private bool HasHansMapChanges => (CombineHansMaps, _hansMapsAsPdf) != _originalHansMaps;
    public string ShopFolderPreview => Journal.ShopFolderPreview(ShopRoot);
    private void ReloadShopRoot()
    {
        _loading = true;
        try { _originalShopRoot = Journal.ShopRoot; ShopRoot = _originalShopRoot ?? ""; }
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
        // Validate both edits before committing either setting; the shared and local stores are independent.
        if (HasRootChanges) _ = WorkflowProjects.SetRoot(_snapshot(), ProjectRoot);
        if (HasShopRootChanges) WorkflowShopFolderPaths.NormalizeRoot(ShopRoot);
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
    }
}
