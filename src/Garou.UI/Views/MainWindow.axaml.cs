using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Garou.Application.Queries;
using Garou.Domain.Models;
using Garou.Infrastructure.Settings;
using Garou.Localization;
using Garou.UI.Views;
using Garou.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.Globalization;

namespace Garou.Views;

public partial class MainWindow : Window
{
    private readonly AppSettingsSession _settingsSession;
    private double _normalWidth;
    private double _normalHeight;
    private PixelPoint _normalPosition;
    private bool _wasMaximized;

    public MainWindow() : this(App.Services.GetRequiredService<AppSettingsSession>())
    {
    }

    public MainWindow(AppSettingsSession settingsSession)
    {
        _settingsSession = settingsSession;
        _wasMaximized = settingsSession.Current.WindowMaximized ?? false;
        
        InitializeComponent();

        Bitmap bitmap = new(AssetLoader.Open(new Uri("avares://Garou.UI/Assets/Garou-24.png")));
        ImageAppIcon.Source = bitmap;

        MediaListBoxFrame.Navigate(typeof(MediaListBoxPage));

        LeftPaneGridSplitter.AddHandler(InputElement.PointerReleasedEvent, OnGridSplitterPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Loaded += OnLoaded;
        ScalingChanged += OnScalingChanged;
        PositionChanged += (_, _) => ScheduleNormalBoundsCache();
        SizeChanged += (_, _) => ScheduleNormalBoundsCache();
        Activated += (_, _) =>
        {
            AppTitle.Opacity = 1;
            ScheduleNormalBoundsCache();
        };
        Deactivated += (_, _) => AppTitle.Opacity = 0.5;
        PropertyChanged += (_, change) =>
        {
            if (change.Property == Window.WindowStateProperty && WindowState is WindowState.Normal or WindowState.Maximized)
            {
                _wasMaximized = WindowState == WindowState.Maximized;
            }
        };
        Closed += OnClosed;

        RestoreWindowBounds();
        RestoreLeftPaneWidth();
    }

    private void RestoreLeftPaneWidth()
    {
        MainLayoutGrid.ColumnDefinitions[0].Width =
            new GridLength(Math.Clamp(_settingsSession.Current.LeftPaneWidth, 180, 500));
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.OnRenderScalingChanged(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
        }
    }

    private void RestoreWindowBounds()
    {
        var settings = _settingsSession.Current;
        if (settings.WindowWidth is { } width && double.IsFinite(width) && width >= MinWidth)
        {
            Width = width;
            _normalWidth = width;
        }

        if (settings.WindowHeight is { } height && double.IsFinite(height) && height >= MinHeight)
        {
            Height = height;
            _normalHeight = height;
        }

        if (settings.WindowX is { } x && settings.WindowY is { } y)
        {
            var position = new PixelPoint(x, y);
            var screens = Screens?.All;
            var screen = screens?.FirstOrDefault(candidate => candidate.WorkingArea.Contains(position))
                ?? screens?.FirstOrDefault();
            if (screen is not null)
            {
                Width = _normalWidth = Math.Min(Width, Math.Max(MinWidth, screen.WorkingArea.Width / screen.Scaling));
                Height = _normalHeight = Math.Min(Height, Math.Max(MinHeight, screen.WorkingArea.Height / screen.Scaling));
                _normalPosition = screen.WorkingArea.Contains(position)
                    ? position
                    : new PixelPoint(screen.WorkingArea.X, screen.WorkingArea.Y);
                Position = _normalPosition;
            }
        }

        if (_wasMaximized)
        {
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
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

    private void ScheduleNormalBoundsCache()
    {
        Dispatcher.UIThread.Post(CacheNormalBounds, DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel disposableViewModel)
        {
            disposableViewModel.Dispose();
        }

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

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var targetPage = MediaListBoxFrame.Content is SettingsPage
            ? typeof(MediaListBoxPage)
            : typeof(SettingsPage);
        MediaListBoxFrame.Navigate(targetPage);
    }

    private void OnMenuFlyoutOpened(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menuFlyout)
        {
            return;
        }

        if (menuFlyout.Target?.DataContext is FolderNode folderNode)
        {
            foreach (var menuItem in menuFlyout.Items.OfType<MenuItem>())
            {
                menuItem.DataContext = folderNode;
            }
        }

        var settings = _settingsSession.Current;
        var localizer = new ResourceLocalizer(CultureInfo.GetCultureInfo(settings.Language == "ja" ? "ja" : "en"));
        foreach (var menuItem in menuFlyout.Items.OfType<MenuItem>())
        {
            if (menuItem.Tag is not string tag)
            {
                continue;
            }

            menuItem.Header = localizer[tag];
        }
    }

    private void OnOpenFolderLocationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: FolderNode node })
        {
            OpenFolderLocation(node.PathText);
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
                viewModel.ReportError(viewModel.StatusText);
            }
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.FolderRestored += OnFolderRestored;
            Dispatcher.UIThread.Post(() => LoadInitialDataAsync(viewModel), DispatcherPriority.Background);
        }
    }

    private async void LoadInitialDataAsync(MainViewModel viewModel)
    {
        await App.StartupTask;
        await viewModel.InitializeAsync(CancellationToken.None);
        MainLayoutGrid.ColumnDefinitions[0].Width = new GridLength(viewModel.LeftPaneWidth);
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
            mainViewModel.ReportError(mainViewModel.StatusText);
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
            mainViewModel.ReportError(mainViewModel.StatusText);
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