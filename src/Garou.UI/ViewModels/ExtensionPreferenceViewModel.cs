using CommunityToolkit.Mvvm.ComponentModel;

namespace Garou.ViewModels;

public sealed partial class ExtensionPreferenceViewModel : ObservableObject
{
    private bool _isIncluded;

    public ExtensionPreferenceViewModel(string extension, bool isIncluded)
    {
        Extension = extension;
        _isIncluded = isIncluded;
    }

    public string Extension { get; }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }
}
