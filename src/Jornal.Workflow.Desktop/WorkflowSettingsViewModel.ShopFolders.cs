using CommunityToolkit.Mvvm.ComponentModel;
using Jornal.Workflow.Core;
using Jornal.Workflow.Infrastructure;

namespace Jornal.Workflow.Desktop;

public partial class WorkflowSettingsViewModel
{
    private string? _originalShopRoot;
    [ObservableProperty] private string _shopRoot = "";
    public bool HasShopRootChanges => ShopRoot != (_originalShopRoot ?? "");
    public string ShopFolderPreview => Journal.ShopFolderPreview(ShopRoot);
    private void ReloadShopRoot()
    {
        _loading = true;
        try { _originalShopRoot = Journal.ShopRoot; ShopRoot = _originalShopRoot ?? ""; }
        finally { _loading = false; }
        Changed();
    }
    partial void OnShopRootChanged(string value)
    { OnPropertyChanged(nameof(ShopFolderPreview)); InputChanged(); }
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
            ReloadShopRoot(); Status = Status is null ? "Папка цеха сохранена на этом компьютере." : "Общая папка проектов и личный путь цеха сохранены.";
        }
    }
}
