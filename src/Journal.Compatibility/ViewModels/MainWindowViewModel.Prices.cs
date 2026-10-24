using Jornal.Core.Pricing;
using Jornal.Infrastructure.Demo;

namespace Jornal.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private IPriceReader? Prices => _source is not null ? _source.Prices :
        orderReader as IPriceReader ?? orderCreator as IPriceReader ?? (orderReader as DemoOrderStore)?.Prices;
    private IPriceEditor? PriceEditor => _source is not null ? _source.PriceEditor :
        orderReader as IPriceEditor ?? orderCreator as IPriceEditor ?? (orderReader as DemoOrderStore)?.Prices;
    public bool CanOpenPrices => CanUseJournal && Prices is not null;
    public PriceEditorViewModel? CreatePriceEditor()
    {
        if (!CanOpenPrices) return null;
        var model = new PriceEditorViewModel(Prices!, PriceEditor, SourceDescription, SourceDetails,
            _source?.Path is null ? PriceStorageKind.Session : _source.IsLocalCopy ? PriceStorageKind.LocalCopy : PriceStorageKind.SharedDatabase);
        model.SavingChanged += (_, _) => IsUpdatingOrders = model.IsSaving;
        return model;
    }
}
