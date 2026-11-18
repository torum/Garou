using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
using Garou.Infrastructure.Settings;
using Garou.Localization;
using Garou.ViewModels;
using Garou.Views.Controls;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Garou.UI.Views;

public partial class MediaListBoxPage : UserControl
{
    private readonly AppSettingsSession _settingsSession;
    private VirtualizingUniformPanel? _mediaPanel;
    private double _detailsPaneWidth = 280;

    public MediaListBoxPage()
    {
        _settingsSession = App.Services.GetRequiredService<AppSettingsSession>();
        DataContext = App.Services.GetRequiredService<MainViewModel>();

        InitializeComponent();
        ApplyLocalizedToolTips();

        MediaListBox.AddHandler(
            InputElement.KeyDownEvent,
            OnMediaListKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        DetailsPaneGridSplitter.AddHandler(
            InputElement.PointerReleasedEvent,
            OnDetailsPaneGridSplitterPointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);

        FolderBreadcrumbBar.ItemClicked += OnBreadcrumbItemClicked;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void ApplyLocalizedToolTips()
    {
        var settings = _settingsSession.Current;
        var localizer = new ResourceLocalizer(
            CultureInfo.GetCultureInfo(settings.Language == "ja" ? "ja" : "en"));

        ToolTip.SetTip(SlideshowPlay, localizer["SlideshowTitle"]);
        ToolTip.SetTip(DetailsPaneToggleButton, localizer["TooltipOpenDetailsPane"]);
        ToolTip.SetTip(ThumbnailSizeSlider, localizer["TooltipAdjustThumbnailSize"]);
        ToolTip.SetTip(ShowFileNamesToggleButton, localizer["TooltipShowFilenames"]);
        ToolTip.SetTip(ShowFileTypeBadgesToggleButton, localizer["TooltipShowFileTypeBadges"]);
        ToolTip.SetTip(IncludeDescendantsToggleButton, localizer["TooltipIncludeSubfolders"]);
        ToolTip.SetTip(IncludeImagesToggleButton, localizer["TooltipIncludeImages"]);
        ToolTip.SetTip(IncludeVideosToggleButton, localizer["TooltipIncludeVideos"]);
        ToolTip.SetTip(IncludeAudioToggleButton, localizer["TooltipIncludeAudio"]);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            _detailsPaneWidth = Math.Clamp(_settingsSession.Current.DetailsPaneWidth, 180, 600);
            ApplyDetailsPaneVisibility(viewModel.IsDetailsPaneOpen);
            viewModel.PropertyChanged += OnMainViewModelPropertyChanged;
        }

        AttachToMediaPanel();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        }

        if (_mediaPanel is not null)
        {
            _mediaPanel.VisibleItemsChanged -= OnVisibleItemsChanged;
            _mediaPanel = null;
        }
    }

    private async void OnMediaSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.LoadSelectedMetadataAsync();
        }
    }

    private async void OnEditXnfoClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var item = (sender as Control)?.DataContext as MediaItemViewModel ?? viewModel.SelectedItem;
        if (item is null)
        {
            return;
        }
        var path = item.File.Path;
        try
        {
            if (!await viewModel.IsMediaAvailableAsync(item.File, viewModel.LifetimeToken, reportError: true))
                return;
            var metadata = await viewModel.LoadXnfoAsync(path, viewModel.LifetimeToken) ?? new XnfoMetadata();
            var edited = await XnfoEditorDialog.ShowAsync(owner, path, metadata, _settingsSession.Current.Language);
            if (edited is not null)
            {
                await viewModel.SaveXnfoAsync(path, edited, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (viewModel.LifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException or InvalidOperationException or NotSupportedException)
        {
            var localizer = new ResourceLocalizer(CultureInfo.GetCultureInfo(_settingsSession.Current.Language == "ja" ? "ja" : "en"));
            viewModel.ReportError(string.Format(localizer["XnfoEditError"], $"{path}: {exception.Message}"));
        }
    }

    private void OnVisibleItemsChanged(object? sender, List<object> items)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.OnVisibleItemsChanged(
                items.OfType<MediaItemViewModel>().Select(item => item.File).ToList());
        }
    }

    private void OnSlideshowPlayClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var localizer = new ResourceLocalizer(
            CultureInfo.GetCultureInfo(_settingsSession.Current.Language == "ja" ? "ja" : "en"));
        var images = viewModel.Items.Where(item => item.MediaType == MediaType.Image)
            .Select(item => item.File).ToArray();
        if (images.Length == 0)
        {
            viewModel.StatusText = localizer["SlideshowNoImages"];
            viewModel.ReportError(viewModel.StatusText);
            return;
        }

        var selectedIndex = Array.FindIndex(images, image => image == viewModel.SelectedItem?.File);
        var window = new SlideshowWindow(images, Math.Max(0, selectedIndex), localizer,
            App.Services.GetRequiredService<Garou.Infrastructure.Thumbnails.FfmpegRunner>());
        window.Show(owner);
    }

    private async void OnMediaDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel { SelectedItem: { File: var mediaFile } } viewModel)
        {
            return;
        }

        await OpenWithDefaultApplicationAsync(mediaFile, viewModel);
    }

    private async void OnMediaListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || DataContext is not MainViewModel { SelectedItem: { File: var mediaFile } } viewModel)
        {
            return;
        }

        e.Handled = true;
        await OpenWithDefaultApplicationAsync(mediaFile, viewModel);
    }

    private async void OnOpenWithDefaultApplicationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: MediaItemViewModel { File: var mediaFile } }
            && DataContext is MainViewModel viewModel)
        {
            await OpenWithDefaultApplicationAsync(mediaFile, viewModel);
        }
    }

    private static async Task OpenWithDefaultApplicationAsync(MediaFile mediaFile, MainViewModel viewModel)
    {
        try
        {
            if (!await viewModel.IsMediaAvailableAsync(mediaFile, viewModel.LifetimeToken, reportError: true))
                return;
            Process.Start(new ProcessStartInfo(mediaFile.Path)
            {
                UseShellExecute = true
            });
        }
        catch (OperationCanceledException) when (viewModel.LifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            viewModel.StatusText = $"Could not open {mediaFile.FileName}: {exception.Message}";
            viewModel.ReportError(viewModel.StatusText);
        }
    }

    private async void OnLaunchWithViewerClick(object? sender, RoutedEventArgs e)
    {
        await LaunchWithConfiguredApplicationAsync(sender, isEditor: false);
    }

    private async void OnLaunchWithEditorClick(object? sender, RoutedEventArgs e)
    {
        await LaunchWithConfiguredApplicationAsync(sender, isEditor: true);
    }

    private async Task LaunchWithConfiguredApplicationAsync(object? sender, bool isEditor)
    {
        if (sender is not MenuItem { DataContext: MediaItemViewModel { File: var mediaFile } }
            || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var applicationPath = GetConfiguredApplicationPath(
            _settingsSession.Current,
            mediaFile.MediaType,
            isEditor);
        var mediaTypeName = mediaFile.MediaType switch
        {
            MediaType.Image => "image",
            MediaType.Video => "video",
            MediaType.Audio => "audio",
            _ => "media"
        };
        var applicationType = $"{mediaTypeName} {(isEditor ? "editor" : "viewer")}";

        if (string.IsNullOrWhiteSpace(applicationPath))
        {
            viewModel.StatusText = $"Configure a {applicationType} application path in settings first.";
            viewModel.ReportError(viewModel.StatusText);
            return;
        }

        try
        {
            if (!await viewModel.IsMediaAvailableAsync(mediaFile, viewModel.LifetimeToken, reportError: true))
                return;
            if (!File.Exists(applicationPath))
            {
                throw new FileNotFoundException("The configured application was not found.", applicationPath);
            }

            var startInfo = new ProcessStartInfo(applicationPath)
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add(mediaFile.Path);
            Process.Start(startInfo);
        }
        catch (OperationCanceledException) when (viewModel.LifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or FileNotFoundException
                or UnauthorizedAccessException)
        {
            viewModel.StatusText =
                $"Could not open {mediaFile.FileName} with the configured {applicationType}: {exception.Message}";
            viewModel.ReportError(viewModel.StatusText);
        }
    }

    private void OnMenuFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menuFlyout)
        {
            return;
        }

        var settings = _settingsSession.Current;
        var localizer = new ResourceLocalizer(
            CultureInfo.GetCultureInfo(settings.Language == "ja" ? "ja" : "en"));

        foreach (var menuItem in menuFlyout.Items.OfType<MenuItem>())
        {
            if (menuItem.Tag is not string tag)
            {
                continue;
            }

            if (tag is "viewer" or "editor")
            {
                if (menuItem.DataContext is not MediaItemViewModel mediaItem)
                {
                    menuItem.IsEnabled = false;
                    continue;
                }

                var isEditor = tag == "editor";
                menuItem.Header = GetMediaActionHeader(localizer, mediaItem.MediaType, isEditor);
                menuItem.IsEnabled = !string.IsNullOrWhiteSpace(
                    GetConfiguredApplicationPath(settings, mediaItem.MediaType, isEditor));
                continue;
            }

            menuItem.Header = localizer[tag];
        }
    }

    private static string GetMediaActionHeader(
        ResourceLocalizer localizer,
        MediaType mediaType,
        bool isEditor) =>
        (mediaType, isEditor) switch
        {
            (MediaType.Image, false) => localizer["ViewImageWithViewer"],
            (MediaType.Video, false) => localizer["PlayVideoWithPlayer"],
            (MediaType.Audio, false) => localizer["PlayAudioWithPlayer"],
            (MediaType.Image, true) => localizer["EditImageWithEditor"],
            (MediaType.Video, true) => localizer["EditVideoWithEditor"],
            (MediaType.Audio, true) => localizer["EditAudioWithEditor"],
            _ => localizer[isEditor ? "OpenWithEditor" : "OpenWithViewer"]
        };

    private static string GetConfiguredApplicationPath(
        AppSettings settings,
        MediaType mediaType,
        bool isEditor) =>
        mediaType switch
        {
            MediaType.Image => isEditor
                ? settings.ImageEditorApplicationPath
                : settings.ImageViewerApplicationPath,
            MediaType.Video => isEditor
                ? settings.VideoEditorApplicationPath
                : settings.VideoViewerApplicationPath,
            MediaType.Audio => isEditor
                ? settings.AudioEditorApplicationPath
                : settings.AudioViewerApplicationPath,
            _ => string.Empty
        };

    private void OnOpenContainingFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MediaItemViewModel { File: var mediaFile } })
        {
            OpenFileLocation(mediaFile.Path);
        }
    }

    private void OnOpenThumbnailFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button
            {
                DataContext: MediaItemViewModel { File.ThumbnailPath: { } thumbnailPath }
            })
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.StatusText = "No cached thumbnail is available for this item";
                viewModel.ReportError(viewModel.StatusText);
            }

            return;
        }

        OpenFileLocation(thumbnailPath);
    }

    private void OpenFileLocation(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directoryPath = Path.GetDirectoryName(fullPath);
            if (directoryPath is null || !Directory.Exists(directoryPath))
            {
                throw new DirectoryNotFoundException(
                    $"The containing folder for '{fullPath}' was not found.");
            }

            Process.Start(new ProcessStartInfo(directoryPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception
                or FileNotFoundException
                or DirectoryNotFoundException)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.StatusText = $"Could not open folder location: {exception.Message}";
                viewModel.ReportError(viewModel.StatusText);
            }
        }
    }

    private void AttachToMediaPanel()
    {
        var panel = MediaListBox.GetVisualDescendants()
            .OfType<VirtualizingUniformPanel>()
            .FirstOrDefault();

        if (ReferenceEquals(panel, _mediaPanel))
        {
            return;
        }

        if (_mediaPanel is not null)
        {
            _mediaPanel.VisibleItemsChanged -= OnVisibleItemsChanged;
        }

        _mediaPanel = panel;
        if (_mediaPanel is not null)
        {
            _mediaPanel.VisibleItemsChanged += OnVisibleItemsChanged;
            _mediaPanel.RefreshVisibleItems();
        }
    }

    private void OnDetailsPaneGridSplitterPointerReleased(
        object? sender,
        PointerReleasedEventArgs e)
    {
        var detailsPaneColumn = MediaDetailsGrid.ColumnDefinitions[2];
        if (detailsPaneColumn.ActualWidth >= 180)
        {
            _detailsPaneWidth = Math.Clamp(detailsPaneColumn.ActualWidth, 180, 600);
            detailsPaneColumn.Width = new GridLength(_detailsPaneWidth);
            _settingsSession.Current.DetailsPaneWidth = _detailsPaneWidth;
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MainViewModel viewModel)
        {
            return;
        }

        if (e.PropertyName == nameof(MainViewModel.IsDetailsPaneOpen))
        {
            ApplyDetailsPaneVisibility(viewModel.IsDetailsPaneOpen);
        }

        if (e.PropertyName == nameof(MainViewModel.SelectedFolder))
        {
            ResetScrollPosition();
        }
    }

    private void OnBreadcrumbItemClicked(object? sender, FABreadcrumbBarItemClickedEventArgs e)
    {
        if (e.Item is FolderBreadcrumb breadcrumb && DataContext is MainViewModel viewModel)
        {
            viewModel.NavigateToBreadcrumb(breadcrumb);
        }
    }

    private void ResetScrollPosition()
    {
        var scrollViewer = MediaListBox.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();
        if (scrollViewer is not null)
        {
            scrollViewer.Offset = new Vector(0, 0);
        }
    }

    private void ApplyDetailsPaneVisibility(bool isOpen)
    {
        MediaDetailsGrid.ColumnDefinitions[2].Width =
            isOpen ? new GridLength(_detailsPaneWidth) : new GridLength(0);
    }
}