using CommunityToolkit.Mvvm.ComponentModel;
using Garou.Domain.Enums;

namespace Garou.ViewModels;

public sealed class MediaSortOption(MediaListSortOrder sortOrder, string label) : ObservableObject
{
    private string _label = label;

    public MediaListSortOrder SortOrder { get; } = sortOrder;

    public string Label
    {
        get => _label;
        internal set => SetProperty(ref _label, value);
    }
}
