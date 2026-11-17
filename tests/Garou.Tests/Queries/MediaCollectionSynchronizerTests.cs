using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.ViewModels;

namespace Garou.Tests.Queries;

[Collection("Avalonia UI")]
public sealed class MediaCollectionSynchronizerTests
{
    [AvaloniaFact]
    public void ListSelectionSurvivesUnchangedRefreshAndInsertion()
    {
        var first = CreateFile(Guid.NewGuid(), "a.jpg");
        var selectedFile = CreateFile(Guid.NewGuid(), "b.jpg");
        var selected = new MediaItemViewModel(selectedFile);
        var items = new ObservableCollection<MediaItemViewModel> { new(first), selected };
        var list = new ListBox { ItemsSource = items, SelectedItem = selected };
        var window = new Window { Content = list, Width = 400, Height = 300 };
        try
        {
            window.Show();
            window.UpdateLayout();
            MediaCollectionSynchronizer.Apply(items, [CreateFile(first.Id, "a.jpg"), CreateFile(selectedFile.Id, "b.jpg")]);
            Assert.Same(selected, list.SelectedItem);

            var added = CreateFile(Guid.NewGuid(), "0.jpg");
            MediaCollectionSynchronizer.Apply(items, [added, first, selectedFile]);
            Assert.Same(selected, list.SelectedItem);
            Assert.Same(selected, items[2]);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public void UnchangedFilesPreserveItemsWithoutCollectionNotifications()
    {
        var first = CreateFile(Guid.NewGuid(), "a.jpg");
        var second = CreateFile(Guid.NewGuid(), "b.jpg");
        var firstItem = new MediaItemViewModel(first);
        var secondItem = new MediaItemViewModel(second);
        var items = new ObservableCollection<MediaItemViewModel> { firstItem, secondItem };
        var notifications = new List<NotifyCollectionChangedAction>();
        items.CollectionChanged += (_, e) => notifications.Add(e.Action);

        MediaCollectionSynchronizer.Apply(items, [CreateFile(first.Id, "a.jpg"), CreateFile(second.Id, "b.jpg")]);

        Assert.Empty(notifications);
        Assert.Same(firstItem, items[0]);
        Assert.Same(secondItem, items[1]);
        Assert.Same(first, firstItem.File);
    }

    [Fact]
    public void ChangedFilesAreUpdatedInPlaceAndBindingsAreNotified()
    {
        var id = Guid.NewGuid();
        var item = new MediaItemViewModel(CreateFile(id, "before.jpg"));
        var items = new ObservableCollection<MediaItemViewModel> { item };
        var properties = new List<string?>();
        var collectionChanges = new List<NotifyCollectionChangedAction>();
        item.PropertyChanged += (_, e) => properties.Add(e.PropertyName);
        items.CollectionChanged += (_, e) => collectionChanges.Add(e.Action);
        var updated = CreateFile(id, "after.jpg");

        MediaCollectionSynchronizer.Apply(items, [updated]);

        Assert.Same(item, Assert.Single(items));
        Assert.Same(updated, item.File);
        Assert.Equal("after.jpg", item.FileName);
        Assert.Contains(nameof(MediaItemViewModel.FileName), properties);
        Assert.Contains(nameof(MediaItemViewModel.Thumbnail), properties);
        Assert.Empty(collectionChanges);
    }

    [Fact]
    public void AddedRemovedAndMovedFilesRetainSurvivingItemsWithoutReset()
    {
        var a = CreateFile(Guid.NewGuid(), "a.jpg");
        var b = CreateFile(Guid.NewGuid(), "b.jpg");
        var c = CreateFile(Guid.NewGuid(), "c.jpg");
        var added = CreateFile(Guid.NewGuid(), "d.jpg");
        var aItem = new MediaItemViewModel(a);
        var bItem = new MediaItemViewModel(b);
        var cItem = new MediaItemViewModel(c);
        var items = new ObservableCollection<MediaItemViewModel> { aItem, bItem, cItem };
        var notifications = new List<NotifyCollectionChangedAction>();
        items.CollectionChanged += (_, e) => notifications.Add(e.Action);

        MediaCollectionSynchronizer.Apply(items, [c, added, a]);

        Assert.Equal(new[] { c.Id, added.Id, a.Id }, items.Select(item => item.File.Id));
        Assert.Same(cItem, items[0]);
        Assert.Same(aItem, items[2]);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, notifications);
        Assert.Contains(NotifyCollectionChangedAction.Remove, notifications);
        Assert.Contains(NotifyCollectionChangedAction.Add, notifications);
        Assert.Contains(NotifyCollectionChangedAction.Move, notifications);
    }

    private static MediaFile CreateFile(Guid id, string name) => new()
    {
        Id = id,
        Path = Path.Combine(Path.GetTempPath(), name),
        FileName = name,
        Extension = ".jpg",
        MediaType = MediaType.Image
    };
}
