using CommunityToolkit.Mvvm.ComponentModel;
using Garou.Application.Queries;

namespace Garou.ViewModels;

public sealed class SearchFieldOption(MediaSearchField field, string label) : ObservableObject
{
    private string _label = label;

    public MediaSearchField Field { get; } = field;

    public string Label
    {
        get => _label;
        internal set => SetProperty(ref _label, value);
    }
}
