using System.Collections.ObjectModel;
using Garou.Domain.Entities;

namespace Garou.ViewModels;

internal static class MediaCollectionSynchronizer
{
    internal static void Apply(ObservableCollection<MediaItemViewModel> items, IReadOnlyList<MediaFile> files)
    {
        var desiredIds = files.Select(file => file.Id).ToHashSet();
        var existing = items.ToDictionary(item => item.File.Id);

        if (items is ResettableObservableCollection<MediaItemViewModel> resettable &&
            HasManyChanges(items, files, desiredIds, existing))
        {
            var replacement = new MediaItemViewModel[files.Count];
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                if (existing.TryGetValue(file.Id, out var item))
                {
                    item.UpdateFile(file);
                    replacement[index] = item;
                }
                else
                {
                    replacement[index] = new MediaItemViewModel(file);
                }
            }

            resettable.ReplaceAll(replacement);
            foreach (var item in existing.Values)
            {
                if (!desiredIds.Contains(item.File.Id))
                    item.Dispose();
            }
            return;
        }

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

    private static bool HasManyChanges(
        IReadOnlyList<MediaItemViewModel> items,
        IReadOnlyList<MediaFile> files,
        HashSet<Guid> desiredIds,
        Dictionary<Guid, MediaItemViewModel> existing)
    {
        const int bulkChangeThreshold = 64;
        var changes = 0;
        var oldIndex = 0;
        var newIndex = 0;
        while (oldIndex < items.Count || newIndex < files.Count)
        {
            if (oldIndex < items.Count && !desiredIds.Contains(items[oldIndex].File.Id))
            {
                oldIndex++;
                changes++;
            }
            else if (newIndex < files.Count && !existing.ContainsKey(files[newIndex].Id))
            {
                newIndex++;
                changes++;
            }
            else if (oldIndex < items.Count && newIndex < files.Count)
            {
                if (items[oldIndex].File.Id != files[newIndex].Id)
                    changes++;
                oldIndex++;
                newIndex++;
            }
            else
            {
                return true;
            }

            if (changes >= bulkChangeThreshold)
                return true;
        }
        return false;
    }
}
