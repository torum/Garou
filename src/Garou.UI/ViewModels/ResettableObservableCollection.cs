using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Garou.ViewModels;

internal sealed class ResettableObservableCollection<T> : ObservableCollection<T>
{
    internal void ReplaceAll(IReadOnlyList<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
