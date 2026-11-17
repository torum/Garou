using System.Collections.ObjectModel;
using Garou.Domain.Entities;

namespace Garou.ViewModels;

internal static class MediaCollectionSynchronizer
{
    internal static void Apply(ObservableCollection<MediaItemViewModel> items, IReadOnlyList<MediaFile> files)
    {
        var desiredIds = files.Select(file => file.Id).ToHashSet();
        var existing = items.ToDictionary(item => item.File.Id);

        for (var index = items.Count - 1; index >= 0; index--)
        {
            var item = items[index];
            if (!desiredIds.Contains(item.File.Id))
            {
                items.RemoveAt(index);
                item.Dispose();
            }
        }

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            if (!existing.TryGetValue(file.Id, out var item))
            {
                items.Insert(index, new MediaItemViewModel(file));
                continue;
            }

            item.UpdateFile(file);
            if (index >= items.Count || !ReferenceEquals(items[index], item))
            {
                items.Move(items.IndexOf(item), index);
            }
        }
    }
}
