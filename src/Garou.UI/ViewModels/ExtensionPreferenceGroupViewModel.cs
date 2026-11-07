namespace Garou.ViewModels;

public sealed record ExtensionPreferenceGroupViewModel(
    string Name,
    IReadOnlyList<ExtensionPreferenceViewModel> Extensions);
