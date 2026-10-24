using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Jornal.Desktop.ViewModels;

namespace Jornal.Desktop.Tables;

internal sealed class OrderRowCollection : ObservableCollection<OrderRowViewModel>
{
    public void ReplaceWith(IReadOnlyList<OrderRowViewModel> rows)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var row in rows) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
