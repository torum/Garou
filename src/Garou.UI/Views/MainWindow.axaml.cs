using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Garou.Application.Queries;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
using Garou.Infrastructure.Settings;
using Garou.Localization;
using Garou.UI.Converters;
using Garou.ViewModels;
using Garou.Views.Controls;

namespace Garou.Views;

public partial class MainWindow : Window
{
    private static readonly double LoadMoreThresholdPixels = 400;
    private readonly AppSettingsSession _settingsSession;
    private VirtualizingUniformPanel? _mediaPanel;
    private double _normalWidth;
    private double _normalHeight;
    private PixelPoint _normalPosition;
    private bool _wasMaximized;

    public MainWindow(AppSettingsSession settingsSession)
    {
        _settingsSession = settingsSession;
        _wasMaximized = settingsSession.Current.WindowMaximized ?? false;
        
        InitializeComponent();

        MediaListBox.AddHandler(InputElement.KeyDownEvent, OnMediaListKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        LeftPaneGridSplitter.AddHandler(InputElement.PointerReleasedEvent, OnGridSplitterPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        MediaListBox.AddHandler(ScrollViewer.ScrollChangedEvent, OnMediaListScrollChanged);
        Loaded += OnLoaded;
        ScalingChanged += OnScalingChanged;
        PositionChanged += (_, _) => CacheNormalBounds();
        SizeChanged += (_, _) => CacheNormalBounds();
        PropertyChanged += (_, change) =>
        {
            if (change.Property == Window.WindowStateProperty)
            {
                if (WindowState is WindowState.Normal or WindowState.Maximized)
                {
                    _wasMaximized = WindowState == WindowState.Maximized;
                }
                CacheNormalBounds();
            }
        };
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RestoreWindowBounds();

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.OnRenderScalingChanged(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        }

        AttachToMediaPanel();
    }

    private async void OnMediaSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.LoadSelectedMetadataAsync();
        }
    }

    private void RestoreWindowBounds()
    {
        var settings = _settingsSession.Current;
        if (settings.WindowWidth is { } width && double.IsFinite(width) && width >= MinWidth)
        {
            Width = width;
        }

        if (settings.WindowHeight is { } height && double.IsFinite(height) && height >= MinHeight)
        {
            Height = height;
        }

        if (settings.WindowX is { } x && settings.WindowY is { } y)
        {
            var position = new PixelPoint(x, y);
            var screen = Screens?.All.FirstOrDefault(candidate => candidate.WorkingArea.Contains(position));
            if (screen is not null)
            {
                Position = position;
                Width = Math.Min(Width, Math.Max(MinWidth, screen.WorkingArea.Width / screen.Scaling));
                Height = Math.Min(Height, Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling));
            }
        }

        CacheNormalBounds();
        if (_wasMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void CacheNormalBounds()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        if (Bounds.Width >= MinWidth && Bounds.Height >= MinHeight)
        {
            _normalWidth = Bounds.Width;
            _normalHeight = Bounds.Height;
        }

        _normalPosition = Position;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel disposableViewModel)
        {
            disposableViewModel.Dispose();
        }

        CacheNormalBounds();
        var settings = _settingsSession.Current;
        if (_normalWidth >= MinWidth && _normalHeight >= MinHeight)
        {
            settings.WindowWidth = _normalWidth;
            settings.WindowHeight = _normalHeight;
            settings.WindowX = _normalPosition.X;
            settings.WindowY = _normalPosition.Y;
        }

        if (WindowState is WindowState.Normal or WindowState.Maximized)
        {
            _wasMaximized = WindowState == WindowState.Maximized;
        }

        settings.WindowMaximized = _wasMaximized;
    }

    private void OnScalingChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.OnRenderScalingChanged(RenderScaling);
        }
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            await new SettingsWindow(viewModel).ShowDialog(this);
        }
    }

    private void OnVisibleItemsChanged(object? sender, List<object> items)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.OnVisibleItemsChanged(items.OfType<MediaItemViewModel>().Select(item => item.File).ToList());
        }
    }

    private void OnMediaListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer scrollViewer || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        if (scrollViewer.Offset.Y + scrollViewer.Viewport.Height < scrollViewer.Extent.Height - LoadMoreThresholdPixels)
        {
            return;
        }

        if (viewModel.LoadMoreCommand.CanExecute(null))
        {
            viewModel.LoadMoreCommand.Execute(null);
        }
    }

    private void OnFolderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var scrollViewer = MediaListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scrollViewer is not null)
        {
            scrollViewer.Offset = new Vector(0, 0);
        }
    }

    private void OnMediaDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainViewModel { SelectedItem: { File: var mediaFile } } viewModel)
        {
            return;
        }

        OpenWithDefaultApplication(mediaFile, viewModel);
    }

    private void OnMediaListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || DataContext is not MainViewModel { SelectedItem: { File: var mediaFile } } viewModel)
        {
            return;
        }

        e.Handled = true;
        OpenWithDefaultApplication(mediaFile, viewModel);
    }

    private void OnOpenWithDefaultApplicationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: MediaItemViewModel { File: var mediaFile } }
            && DataContext is MainViewModel viewModel)
        {
            OpenWithDefaultApplication(mediaFile, viewModel);
        }
    }

    private static void OpenWithDefaultApplication(MediaFile mediaFile, MainViewModel viewModel)
    {
        try
        {
            Process.Start(new ProcessStartInfo(mediaFile.Path)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            viewModel.StatusText = $"Could not open {mediaFile.FileName}: {exception.Message}";
        }
    }

    private void OnLaunchWithViewerClick(object? sender, RoutedEventArgs e)
    {
        LaunchWithConfiguredApplication(sender, isEditor: false);
    }

    private void OnLaunchWithEditorClick(object? sender, RoutedEventArgs e)
    {
        LaunchWithConfiguredApplication(sender, isEditor: true);
    }

    private void LaunchWithConfiguredApplication(object? sender, bool isEditor)
    {
        if (sender is not MenuItem { DataContext: MediaItemViewModel { File: var mediaFile } }
            || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var applicationPath = GetConfiguredApplicationPath(_settingsSession.Current, mediaFile.MediaType, isEditor);
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
            return;
        }

        try
        {
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
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException or UnauthorizedAccessException)
        {
            viewModel.StatusText = $"Could not open {mediaFile.FileName} with the configured {applicationType}: {exception.Message}";
        }
    }

    private void OnContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
        {
            return;
        }

        var settings = _settingsSession.Current;
        var localizer = new ResourceLocalizer(CultureInfo.GetCultureInfo(settings.Language == "ja" ? "ja" : "en"));
        foreach (var menuItem in contextMenu.Items.OfType<MenuItem>())
        {
            if (menuItem.Tag is not string tag)
            {
                continue;
            }

            if (tag is "viewer" or "editor")
            {
                if (contextMenu.DataContext is not MediaItemViewModel mediaItem)
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

    private static string GetMediaActionHeader(ResourceLocalizer localizer, MediaType mediaType, bool isEditor) =>
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

    private static string GetConfiguredApplicationPath(AppSettings settings, MediaType mediaType, bool isEditor) => mediaType switch
    {
        MediaType.Image => isEditor ? settings.ImageEditorApplicationPath : settings.ImageViewerApplicationPath,
        MediaType.Video => isEditor ? settings.VideoEditorApplicationPath : settings.VideoViewerApplicationPath,
        MediaType.Audio => isEditor ? settings.AudioEditorApplicationPath : settings.AudioViewerApplicationPath,
        _ => string.Empty
    };

    private void OnOpenContainingFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: MediaItemViewModel { File: var mediaFile } })
        {
            OpenFileLocation(mediaFile.Path);
        }
    }

    private void OnOpenFolderLocationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: FolderNode node })
        {
            OpenFolderLocation(node.PathText);
        }
    }

    private void OnOpenThumbnailFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: MediaItemViewModel { File.ThumbnailPath: { } thumbnailPath } })
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.StatusText = "No cached thumbnail is available for this item";
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
                throw new DirectoryNotFoundException($"The containing folder for '{fullPath}' was not found.");
            }

            Process.Start(new ProcessStartInfo(directoryPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.StatusText = $"Could not open folder location: {exception.Message}";
            }
        }
    }

    private void OpenFolderLocation(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException($"Folder '{fullPath}' was not found.");
            }

            Process.Start(new ProcessStartInfo(fullPath)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            if (DataContext is MainViewModel viewModel)
            {
                viewModel.StatusText = $"Could not open folder location: {exception.Message}";
            }
        }
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.FolderRestored += OnFolderRestored;
            await viewModel.InitializeAsync(CancellationToken.None);
            AttachToMediaPanel();
            _mediaPanel?.RefreshVisibleItems();
            MainLayoutGrid.ColumnDefinitions[0].Width = new GridLength(viewModel.LeftPaneWidth);
        }
    }

    private void AttachToMediaPanel()
    {
        var panel = MediaListBox.GetVisualDescendants().OfType<VirtualizingUniformPanel>().FirstOrDefault();
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
        }
    }

    private void OnGridSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && MainLayoutGrid.ColumnDefinitions[0].ActualWidth >= 180)
        {
            viewModel.LeftPaneWidth = Math.Clamp(MainLayoutGrid.ColumnDefinitions[0].ActualWidth, 180, 500);
        }
    }

    private void OnFolderRestored(FolderNode? node)
    {
        Dispatcher.UIThread.Post(() => RestoreTreeState(node), DispatcherPriority.Loaded);
    }

    private void RestoreTreeState(FolderNode? selectedNode)
    {
        if (DataContext is MainViewModel viewModel)
        {
            ExpandSaved(FolderTreeView, viewModel.ExpandedFolderIds);
        }

        if (selectedNode is not null)
        {
            ExpandAndSelect(selectedNode);
        }
    }

    private void ExpandSaved(ItemsControl container, HashSet<Guid> expandedIds)
    {
        foreach (var node in container.ItemsSource?.OfType<FolderNode>() ?? [])
        {
            if (container.ContainerFromItem(node) is not TreeViewItem item)
            {
                continue;
            }

            item.IsExpanded = expandedIds.Contains(node.Folder.Id);
            item.UpdateLayout();
            ExpandSaved(item, expandedIds);
        }
    }

    private void OnTreeItemExpandedChanged(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TreeViewItem { DataContext: FolderNode node } item && DataContext is MainViewModel viewModel)
        {
            viewModel.SetFolderExpanded(node.Folder.Id, item.IsExpanded);
        }
    }

    private async void OnClearFolderThumbnailCacheClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: FolderNode node } && DataContext is MainViewModel viewModel)
        {
            await viewModel.ClearFolderThumbnailCacheAsync(node, CancellationToken.None);
            return;
        }

        if (DataContext is MainViewModel mainViewModel)
        {
            mainViewModel.StatusText = "Could not determine the folder for the thumbnail cache command";
        }
    }

    private async void OnRemoveFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: FolderNode node } && DataContext is MainViewModel viewModel)
        {
            await viewModel.RemoveFolderAsync(node, CancellationToken.None);
            return;
        }

        if (DataContext is MainViewModel mainViewModel)
        {
            mainViewModel.StatusText = "Could not determine the folder for the remove command";
        }
    }

    private void ExpandAndSelect(FolderNode node)
    {
        var ancestors = new List<FolderNode>();
        if (!FindPath(FolderTreeView.ItemsSource?.OfType<FolderNode>(), node, ancestors))
        {
            return;
        }

        ItemsControl container = FolderTreeView;
        foreach (var ancestor in ancestors)
        {
            var item = container.ContainerFromItem(ancestor) as TreeViewItem;
            if (item is null)
            {
                return;
            }

            item.IsExpanded = true;
            item.UpdateLayout();
            container = item;
        }

        if (container.ContainerFromItem(node) is TreeViewItem selected)
        {
            selected.IsSelected = true;
            selected.BringIntoView();
        }
    }

    private static bool FindPath(IEnumerable<FolderNode>? nodes, FolderNode target, List<FolderNode> path)
    {
        if (nodes is null)
        {
            return false;
        }

        foreach (var candidate in nodes)
        {
            if (ReferenceEquals(candidate, target))
            {
                return true;
            }

            path.Add(candidate);
            if (FindPath(candidate.Children, target, path))
            {
                return true;
            }

            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select media folder",
            AllowMultiple = false,
        });

        if (folders.Count == 0 || DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var path = folders[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await viewModel.AddFolderAsync(path, CancellationToken.None);
    }
}