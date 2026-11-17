using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Garou.ViewModels;

internal class ResettableObservableCollection<T> : ObservableCollection<T>
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

internal sealed class MediaItemCollection : ResettableObservableCollection<MediaItemViewModel>
{
    private Dictionary<Guid, MediaItemViewModel>? _itemsById;

    public MediaItemCollection()
    {
        CollectionChanged += (_, _) => _itemsById = null;
    }

    internal MediaItemViewModel? FindById(Guid id)
    {
        if (_itemsById is null)
        {
            _itemsById = new Dictionary<Guid, MediaItemViewModel>(Count);
            foreach (var item in this)
                _itemsById.TryAdd(item.File.Id, item);
        }
        return _itemsById.GetValueOrDefault(id);
    }
}
