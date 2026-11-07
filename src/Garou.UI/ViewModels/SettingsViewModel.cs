using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Garou.Domain.Models;
using Garou.Localization;
using Garou.Infrastructure.Settings;

namespace Garou.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private const long BytesPerMegabyte = 1024L * 1024;
    private const double MaximumCacheLimitMegabytes = 1023;
    private readonly AppSettingsSession _settingsSession;
    private readonly SettingsStore _settingsStore;
    private ResourceLocalizer _localizer = new(CultureInfo.GetCultureInfo("en"));
    private double _cacheLimitMegabytes = 900;
    private bool _scanSubfolders = true;
    private string _imageViewerApplicationPath = string.Empty;
    private string _imageEditorApplicationPath = string.Empty;
    private string _videoViewerApplicationPath = string.Empty;
    private string _videoEditorApplicationPath = string.Empty;
    private string _audioViewerApplicationPath = string.Empty;
    private string _audioEditorApplicationPath = string.Empty;

    public SettingsViewModel(AppSettingsSession settingsSession, SettingsStore settingsStore)
    {
        _settingsSession = settingsSession;
        _settingsStore = settingsStore;
        Extensions = new ObservableCollection<ExtensionPreferenceViewModel>(
            MediaFileExtensions.All.Select(extension => new ExtensionPreferenceViewModel(extension, true)));
    }

    public IReadOnlyList<string> SupportedLanguages { get; } = ["en", "ja"];

    public ObservableCollection<ExtensionPreferenceViewModel> Extensions { get; }

    public IReadOnlyList<ExtensionPreferenceGroupViewModel> ExtensionGroups =>
    [
        new(_localizer["ImageMediaType"], Extensions.Where(extension => MediaFileExtensions.Images.Contains(extension.Extension, StringComparer.OrdinalIgnoreCase)).ToArray()),
        new(_localizer["VideoMediaType"], Extensions.Where(extension => MediaFileExtensions.Videos.Contains(extension.Extension, StringComparer.OrdinalIgnoreCase)).ToArray()),
        new(_localizer["AudioMediaType"], Extensions.Where(extension => MediaFileExtensions.Audio.Contains(extension.Extension, StringComparer.OrdinalIgnoreCase)).ToArray())
    ];

    [ObservableProperty]
    public partial string SelectedLanguage { get; set; } = "en";

    partial void OnSelectedLanguageChanged(string value)
    {
        _localizer = new ResourceLocalizer(CultureInfo.GetCultureInfo(value == "ja" ? "ja" : "en"));
        OnPropertyChanged(nameof(SettingsTitle));
        OnPropertyChanged(nameof(LanguageLabel));
        OnPropertyChanged(nameof(ExtensionsLabel));
        OnPropertyChanged(nameof(ExtensionsDescription));
        OnPropertyChanged(nameof(ExtensionGroups));
        OnPropertyChanged(nameof(CacheLimitLabel));
        OnPropertyChanged(nameof(CacheLimitUnit));
        OnPropertyChanged(nameof(ImageViewerApplicationPathLabel));
        OnPropertyChanged(nameof(ImageEditorApplicationPathLabel));
        OnPropertyChanged(nameof(VideoViewerApplicationPathLabel));
        OnPropertyChanged(nameof(VideoEditorApplicationPathLabel));
        OnPropertyChanged(nameof(AudioViewerApplicationPathLabel));
        OnPropertyChanged(nameof(AudioEditorApplicationPathLabel));
        OnPropertyChanged(nameof(ImageMediaTypeLabel));
        OnPropertyChanged(nameof(VideoMediaTypeLabel));
        OnPropertyChanged(nameof(AudioMediaTypeLabel));
        OnPropertyChanged(nameof(ApplicationPathDescription));
        OnPropertyChanged(nameof(SubfoldersLabel));
        OnPropertyChanged(nameof(SaveLabel));
        OnPropertyChanged(nameof(CloseLabel));
    }

    public string SettingsTitle => _localizer["SettingsTitle"];

    public string LanguageLabel => _localizer["LanguageLabel"];

    public string ExtensionsLabel => _localizer["IncludedExtensionsLabel"];

    public string ExtensionsDescription => _localizer["IncludedExtensionsDescription"];

    public string CacheLimitLabel => _localizer["ThumbnailCacheLimitLabel"];

    public string CacheLimitUnit => _localizer["MegabytesUnit"];

    public string ImageViewerApplicationPathLabel => _localizer["ImageViewerApplicationPathLabel"];

    public string ImageEditorApplicationPathLabel => _localizer["ImageEditorApplicationPathLabel"];

    public string VideoViewerApplicationPathLabel => _localizer["VideoViewerApplicationPathLabel"];

    public string VideoEditorApplicationPathLabel => _localizer["VideoEditorApplicationPathLabel"];

    public string AudioViewerApplicationPathLabel => _localizer["AudioViewerApplicationPathLabel"];

    public string AudioEditorApplicationPathLabel => _localizer["AudioEditorApplicationPathLabel"];

    public string ImageMediaTypeLabel => _localizer["ImageMediaType"];

    public string VideoMediaTypeLabel => _localizer["VideoMediaType"];

    public string AudioMediaTypeLabel => _localizer["AudioMediaType"];

    public string ApplicationPathDescription => _localizer["ApplicationPathDescription"];

    public string SubfoldersLabel => _localizer["IncludeSubfoldersLabel"];

    public string ScanSubfoldersLabel => _localizer["ScanSubfoldersLabel"];

    public string ScanSubfoldersDescription => _localizer["ScanSubfoldersDescription"];

    public bool ScanSubfolders
    {
        get => _scanSubfolders;
        set => SetProperty(ref _scanSubfolders, value);
    }

    public string SaveLabel => _localizer["SaveSettingsLabel"];

    public string CloseLabel => _localizer["CloseLabel"];

    public string ImageViewerApplicationPath
    {
        get => _imageViewerApplicationPath;
        set => SetProperty(ref _imageViewerApplicationPath, value);
    }

    public string ImageEditorApplicationPath
    {
        get => _imageEditorApplicationPath;
        set => SetProperty(ref _imageEditorApplicationPath, value);
    }

    public string VideoViewerApplicationPath
    {
        get => _videoViewerApplicationPath;
        set => SetProperty(ref _videoViewerApplicationPath, value);
    }

    public string VideoEditorApplicationPath
    {
        get => _videoEditorApplicationPath;
        set => SetProperty(ref _videoEditorApplicationPath, value);
    }

    public string AudioViewerApplicationPath
    {
        get => _audioViewerApplicationPath;
        set => SetProperty(ref _audioViewerApplicationPath, value);
    }

    public string AudioEditorApplicationPath
    {
        get => _audioEditorApplicationPath;
        set => SetProperty(ref _audioEditorApplicationPath, value);
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    public double CacheLimitMegabytes
    {
        get => _cacheLimitMegabytes;
        set
        {
            if (SetProperty(ref _cacheLimitMegabytes, value))
            {
                OnPropertyChanged(nameof(CacheLimitValue));
            }
        }
    }

    public string CacheLimitValue => $"{CacheLimitMegabytes:0} {CacheLimitUnit}";

    [RelayCommand]
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settingsSession.Current;
        SelectedLanguage = SupportedLanguages.Contains(settings.Language, StringComparer.OrdinalIgnoreCase)
            ? settings.Language
            : "en";
        CacheLimitMegabytes = Math.Clamp(settings.ThumbnailCacheLimitBytes / (double)BytesPerMegabyte, 1, MaximumCacheLimitMegabytes);
        ImageViewerApplicationPath = settings.ImageViewerApplicationPath;
        ImageEditorApplicationPath = settings.ImageEditorApplicationPath;
        VideoViewerApplicationPath = settings.VideoViewerApplicationPath;
        VideoEditorApplicationPath = settings.VideoEditorApplicationPath;
        AudioViewerApplicationPath = settings.AudioViewerApplicationPath;
        AudioEditorApplicationPath = settings.AudioEditorApplicationPath;
        ScanSubfolders = settings.ScanSubfolders;
        var includedExtensions = settings.IncludedExtensions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in Extensions)
        {
            extension.IsIncluded = includedExtensions.Contains(extension.Extension);
        }

        StatusText = string.Empty;
        return Task.CompletedTask;
    }

    [RelayCommand]
    public async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        if (!double.IsFinite(CacheLimitMegabytes) || CacheLimitMegabytes is < 1 or > MaximumCacheLimitMegabytes)
        {
            StatusText = _localizer["InvalidCacheLimit"];
            return;
        }

        var includedExtensions = Extensions
            .Where(extension => extension.IsIncluded)
            .Select(extension => extension.Extension)
            .ToList();
        if (includedExtensions.Count == 0)
        {
            StatusText = _localizer["SelectAtLeastOneExtension"];
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settingsSession.Current;
        settings.Language = SelectedLanguage;
        settings.ThumbnailCacheLimitBytes = (long)Math.Round(CacheLimitMegabytes, MidpointRounding.AwayFromZero) * BytesPerMegabyte;
        settings.IncludedExtensions = includedExtensions;
        settings.ScanSubfolders = ScanSubfolders;
        settings.ImageViewerApplicationPath = ImageViewerApplicationPath.Trim();
        settings.ImageEditorApplicationPath = ImageEditorApplicationPath.Trim();
        settings.VideoViewerApplicationPath = VideoViewerApplicationPath.Trim();
        settings.VideoEditorApplicationPath = VideoEditorApplicationPath.Trim();
        settings.AudioViewerApplicationPath = AudioViewerApplicationPath.Trim();
        settings.AudioEditorApplicationPath = AudioEditorApplicationPath.Trim();
        await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        StatusText = _localizer["SettingsSaved"];
    }
}
