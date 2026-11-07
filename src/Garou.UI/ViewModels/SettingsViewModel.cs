using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Garou.Infrastructure.Settings;

namespace Garou.ViewModels;

public partial class SettingsViewModel(AppSettingsSession settingsSession) : ObservableObject
{
    public IReadOnlyList<string> SupportedLanguages { get; } = ["en", "ja"];

    [ObservableProperty]
    public partial string SelectedLanguage { get; set; } = "en";

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [RelayCommand]
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SelectedLanguage = settingsSession.Current.Language;
        return Task.CompletedTask;
    }

    [RelayCommand]
    public Task SaveLanguageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        settingsSession.Current.Language = SelectedLanguage;
        StatusText = "Language change takes effect on next launch.";
        return Task.CompletedTask;
    }
}
