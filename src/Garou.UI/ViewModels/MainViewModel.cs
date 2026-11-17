using System.Globalization;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Garou.Application.Libraries;
using Garou.Application.Queries;
using Garou.Domain.Entities;
using Garou.Domain.Models;
using Garou.Domain.Abstractions;
using Garou.Infrastructure.Metadata;
using Garou.Infrastructure.Settings;
using Garou.Infrastructure.Sync;
using Avalonia.Threading;
using Garou.Localization;
using Garou.UI.Models;

namespace Garou.ViewModels;

public partial class MainViewModel(
    MediaQueryService queries,
    LibraryCommands libraryCommands,
    LibraryOperationGate operationGate,
    SettingsViewModel settings,
    AppSettingsSession settingsSession,
    IThumbnailGeneratorService thumbnailGenerator,
    IMediaFileRepository mediaFiles,
    IFolderRepository folders,
    IMetadataExtractionService metadataExtraction,
    XnfoIndexService xnfoIndex,
    MediaIndexerService indexer) : ObservableObject, IDisposable
{
    private const int SearchDebounceMilliseconds = 300;
    private const int ThumbnailRegenerationDelayMilliseconds = 300;
    private const double MinThumbnailSize = 80;
    private const double MaxThumbnailSize = 320;
    private const int SmallThumbnailSize = 200;
    private const int LargeThumbnailSize = 320;
    private const double LargeThumbnailSizeThreshold = 190;
    private double _renderScaling = 1;
    private const double MinLeftPaneWidth = 180;
    private const double MaxLeftPaneWidth = 500;
    private readonly SemaphoreSlim _mediaLoadSemaphore = new(1, 1);
    private readonly SemaphoreSlim _scanRefreshSemaphore = new(1, 1);
    private Guid? _selectedFolderId;
    private CancellationTokenSource? _reloadCts;
    private CancellationTokenSource? _selectedMetadataCts;
    private CancellationTokenSource? _folderAvailabilityCts;
    private bool _isInitializing;
    private bool _isGeneratingThumbnails;
    private bool _suppressSelectedFolderReload;
    private bool _showFileTypeBadges = true;
    private bool _includeImages = true;
    private bool _includeVideos = true;
    private bool _includeAudio = true;
    private readonly ResettableObservableCollection<MediaItemViewModel> _items = new();
    private IReadOnlyList<MediaFile> _visibleThumbnailFiles = [];
    private readonly object _thumbnailRegenerationLock = new();
    private long _visibleThumbnailVersion;
    private bool _thumbnailRegenerationWorkerRunning;
    private bool _xnfoSubscribed;
    private bool _disposed;
    private readonly HashSet<string> _offlineLibraryPaths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private int _scanVersion;
    private int _activeScanOperations;
    private readonly CancellationTokenSource _lifetimeCts = new();

    public ObservableCollection<MediaItemViewModel> Items => _items;
    public CancellationToken LifetimeToken => _lifetimeCts.Token;

    public void Dispose()
    {
        _disposed = true;
        _lifetimeCts.Cancel();
        _folderAvailabilityCts?.Cancel();
        if (_xnfoSubscribed)
        {
            indexer.AutomaticScanStarted -= OnAutomaticScanStarted;
            indexer.AutomaticScanProgress -= OnAutomaticScanProgress;
            indexer.AutomaticScanFinished -= OnAutomaticScanFinished;
            indexer.IndexingIssue -= OnIndexingIssue;
            indexer.ItemsSkipped -= OnItemsSkipped;
            xnfoIndex.IndexingError -= OnXnfoIndexingError;
            xnfoIndex.MetadataChanged -= OnXnfoMetadataChanged;
            _xnfoSubscribed = false;
        }
        _selectedMetadataCts?.Cancel();
        _selectedMetadataCts?.Dispose();

        foreach (var item in Items)
        {
            item.Dispose();
        }

        Items.Clear();
    }

    public ObservableCollection<FolderNode> FolderTree { get; } = [];

    public ObservableCollection<string> ExcludedFolders { get; } = [];

    public SettingsViewModel Settings { get; } = settings;

    [ObservableProperty]
    public partial bool IsDetailsPaneOpen { get; set; } = false;

    [ObservableProperty]
    public partial bool ShowFileNames { get; set; } = true;

    public bool ShowFileTypeBadges
    {
        get => _showFileTypeBadges;
        set
        {
            if (SetProperty(ref _showFileTypeBadges, value))
            {
                UpdateSettingsSession();
            }
        }
    }

    public bool IncludeImages
    {
        get => _includeImages;
        set
        {
            if (SetProperty(ref _includeImages, value))
            {
                OnMediaTypeFilterChanged();
            }
        }
    }

    public bool IncludeVideos
    {
        get => _includeVideos;
        set
        {
            if (SetProperty(ref _includeVideos, value))
            {
                OnMediaTypeFilterChanged();
            }
        }
    }

    public bool IncludeAudio
    {
        get => _includeAudio;
        set
        {
            if (SetProperty(ref _includeAudio, value))
            {
                OnMediaTypeFilterChanged();
            }
        }
    }

    private MediaTypeFilter SelectedMediaTypeFilter
    {
        get
        {
            var filter = MediaTypeFilter.Any;
            if (IncludeImages)
            {
                filter |= MediaTypeFilter.Image;
            }

            if (IncludeVideos)
            {
                filter |= MediaTypeFilter.Video;
            }

            if (IncludeAudio)
            {
                filter |= MediaTypeFilter.Audio;
            }

            return filter == MediaTypeFilter.Any ? MediaTypeFilter.None : filter;
        }
    }

    private void OnMediaTypeFilterChanged()
    {
        UpdateSettingsSession();
        if (!_isInitializing)
        {
            ScheduleReload(0);
        }
    }

    [ObservableProperty]
    public partial bool SortFilesByFolder { get; set; }

    partial void OnSortFilesByFolderChanged(bool value)
    {
        UpdateSettingsSession();
        if (!_isInitializing)
        {
            ScheduleReload(0);
        }
    }

    [ObservableProperty]
    public partial bool IncludeDescendants { get; set; } = true;

    partial void OnIncludeDescendantsChanged(bool value)
    {
        UpdateSettingsSession();
        if (!_isInitializing)
        {
            ScheduleReload(0);
        }
    }

    public async Task RemoveFolderAsync(FolderNode folderNode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folderNode);

        var folderIds = new HashSet<Guid>();
        CollectFolderIds(folderNode, folderIds);
        var afterId = Guid.Empty;
        var removedMediaCount = 0;

        try
        {
            using var operation = await operationGate.EnterAsync(cancellationToken);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await queries.SearchPageAsync(new MediaQuery(
                    FolderId: folderNode.Folder.Id,
                    SearchText: null,
                    MediaType: MediaTypeFilter.Any,
                    SortField: MediaSortField.Id,
                    Descending: false,
                    AfterId: afterId,
                    PageSize: 500,
                    IncludeDescendants: true), cancellationToken);

                foreach (var file in page.Items)
                {
                    await thumbnailGenerator.ClearCacheAsync(file, cancellationToken);
                    removedMediaCount++;
                }

                if (page.NextAfterId is not { } nextAfterId || nextAfterId == afterId)
                {
                    break;
                }

                afterId = nextAfterId;
            }

            await folders.DeleteSubtreeAsync(folderNode.Folder.Id, cancellationToken);
            await libraryCommands.ExcludeFolderAsync(folderNode.PathText, cancellationToken);
            await libraryCommands.RemoveLibraryByRootAsync(folderNode.PathText, cancellationToken);
            operationGate.SuppressPath(folderNode.PathText);
            ExpandedFolderIds.ExceptWith(folderIds);
            if (SelectedItem?.File.FolderId is { } selectedMediaFolderId && folderIds.Contains(selectedMediaFolderId))
            {
                SelectedItem = null;
            }

            var selectedFolderId = _selectedFolderId is { } id && folderIds.Contains(id)
                ? null
                : _selectedFolderId;
            await LoadFolderTreeAsync(cancellationToken);

            _suppressSelectedFolderReload = true;
            SelectedFolder = selectedFolderId is { } selectedId
                ? FindFolderNode(FolderTree, selectedId)
                : null;
            _suppressSelectedFolderReload = false;
            UpdateSettingsSession();

            await ReloadAsync(_selectedFolderId, cancellationToken);
            StatusText = $"Removed {folderNode.PathText} and {removedMediaCount} indexed media item(s). Source files were not deleted. Restore excluded subfolders in Settings, or re-add a removed library root.";
            Log(StatusText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            StatusText = $"Could not remove {folderNode.PathText}: {exception.Message}";
            ReportError(StatusText);
            Log(StatusText);
        }
    }

    private static void CollectFolderIds(FolderNode node, HashSet<Guid> folderIds)
    {
        folderIds.Add(node.Folder.Id);
        foreach (var child in node.Children)
        {
            CollectFolderIds(child, folderIds);
        }
    }

    partial void OnShowFileNamesChanged(bool value)
    {
        OnPropertyChanged(nameof(ThumbnailTileHeight));
        UpdateSettingsSession();
    }

    partial void OnIsDetailsPaneOpenChanged(bool value)
    {
        Log($"Details pane open={value}");
        UpdateSettingsSession();
    }

    [ObservableProperty]
    public partial double ThumbnailSize { get; set; } = 120;

    public double ThumbnailTileWidth => ThumbnailSize + 32;

    public double ThumbnailTileHeight => ThumbnailSize + (ShowFileNames ? 64 : 32);

    public void OnRenderScalingChanged(double renderScaling)
    {
        if (renderScaling <= 0 || _renderScaling == renderScaling)
        {
            return;
        }

        _renderScaling = renderScaling;
        RequestVisibleThumbnailRegeneration();
    }

    [ObservableProperty]
    public partial double LeftPaneWidth { get; set; } = 260;

    partial void OnLeftPaneWidthChanged(double value) => UpdateSettingsSession();

    partial void OnThumbnailSizeChanged(double value)
    {
        OnPropertyChanged(nameof(ThumbnailTileWidth));
        OnPropertyChanged(nameof(ThumbnailTileHeight));

        UpdateSettingsSession();

        if (!_isInitializing)
        {
            RequestVisibleThumbnailRegeneration();
        }
    }

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    public partial SearchFieldOption SelectedSearchField { get; set; } = settings.SearchFieldOptions[0];

    [ObservableProperty]
    public partial FolderNode? SelectedFolder { get; set; }

    public IReadOnlyList<FolderBreadcrumb> SelectedFolderBreadcrumbs { get; private set; } = [];

    [ObservableProperty]
    public partial MediaItemViewModel? SelectedItem { get; set; }

    public void NavigateToBreadcrumb(FolderBreadcrumb breadcrumb)
    {
        var node = FindFolderNodeByPath(FolderTree, breadcrumb.Path);
        if (node is not null)
        {
            SelectedFolder = node;
        }
    }

    private static IReadOnlyList<FolderBreadcrumb> BuildBreadcrumbs(string? path)
    {
        var breadcrumbs = new List<FolderBreadcrumb>();
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var title = Path.GetFileName(current);
            breadcrumbs.Add(new FolderBreadcrumb(string.IsNullOrEmpty(title) ? current : title, current));
        }

        breadcrumbs.Reverse();
        return breadcrumbs;
    }

    public Task<XnfoMetadata?> LoadXnfoAsync(string path, CancellationToken cancellationToken) =>
        xnfoIndex.LoadAsync(path, cancellationToken);

    public async Task SaveXnfoAsync(string path, XnfoMetadata metadata, CancellationToken cancellationToken)
    {
        using (await operationGate.EnterAsync(cancellationToken))
        {
            if (!File.Exists(path) || await mediaFiles.GetByPathAsync(path, cancellationToken) is null)
            {
                throw new FileNotFoundException("The media file is no longer available or indexed.", path);
            }
            await xnfoIndex.SaveAsync(path, metadata, cancellationToken);
        }
        await ReloadAsync(_selectedFolderId, cancellationToken);
    }

    public async Task LoadSelectedMetadataAsync()
    {
        _selectedMetadataCts?.Cancel();
        _selectedMetadataCts?.Dispose();
        _selectedMetadataCts = null;

        var selectedItem = SelectedItem;
        if (selectedItem is null || selectedItem.MediaType == Garou.Domain.Enums.MediaType.Unknown)
        {
            selectedItem?.SetDetailedTags([]);
            return;
        }

        selectedItem.SetDetailedTags([]);
        var cancellation = new CancellationTokenSource();
        _selectedMetadataCts = cancellation;

        try
        {
            if (!await IsMediaAvailableAsync(selectedItem.File, cancellation.Token))
                return;
            var metadata = await metadataExtraction.ExtractAsync(selectedItem.File.Path, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(SelectedItem, selectedItem))
            {
                selectedItem.SetDetailedTags(GetMetadataTags(metadata));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var stillAvailable = await Task.Run(() => File.Exists(selectedItem.File.Path));
            if (cancellation.IsCancellationRequested || !ReferenceEquals(SelectedItem, selectedItem))
                return;
            if (!stillAvailable)
            {
                StatusText = string.Format(Localize("MediaUnavailable"), selectedItem.File.FileName);
                return;
            }
            var message = $"Metadata extraction failed for '{selectedItem.File.Path}': {ex.Message}";
            ReportError(message);
            Debug.WriteLine($"[MainViewModel] {message}");
        }
        finally
        {
            if (ReferenceEquals(_selectedMetadataCts, cancellation))
            {
                _selectedMetadataCts.Dispose();
                _selectedMetadataCts = null;
            }
        }
    }

    private static IReadOnlyList<MediaMetadataTag> GetMetadataTags(MediaMetadata metadata)
    {
        var tags = metadata.Tags.ToList();
        AddMetadataTag(tags, "Dimensions", metadata.Width is { } width && metadata.Height is { } height ? $"{width} x {height}" : null);
        AddMetadataTag(tags, "Duration", metadata.DurationSeconds is { } duration ? TimeSpan.FromSeconds(duration).ToString() : null);
        AddMetadataTag(tags, "Codec", metadata.Codec);
        AddMetadataTag(tags, "Artist", metadata.Artist);
        AddMetadataTag(tags, "Album", metadata.Album);
        AddMetadataTag(tags, "Track", metadata.Track?.ToString(CultureInfo.CurrentCulture));
        AddMetadataTag(tags, "Camera make", metadata.CameraMake);
        AddMetadataTag(tags, "Camera model", metadata.CameraModel);
        AddMetadataTag(tags, "Latitude", metadata.Latitude?.ToString("F6", CultureInfo.CurrentCulture));
        AddMetadataTag(tags, "Longitude", metadata.Longitude?.ToString("F6", CultureInfo.CurrentCulture));
        AddMetadataTag(tags, "Date taken", metadata.DateTaken);
        AddMetadataTag(tags, "ISO", metadata.IsoSpeed);
        AddMetadataTag(tags, "Aperture", metadata.Aperture);
        AddMetadataTag(tags, "Shutter speed", metadata.ShutterSpeed);
        AddMetadataTag(tags, "Exposure compensation", metadata.ExposureCompensation);
        AddMetadataTag(tags, "Focal length", metadata.FocalLength);
        AddMetadataTag(tags, "35 mm equivalent", metadata.FocalLength35mm);
        AddMetadataTag(tags, "Flash", metadata.Flash);
        AddMetadataTag(tags, "Exposure program", metadata.ExposureProgram);
        AddMetadataTag(tags, "Metering mode", metadata.MeteringMode);
        AddMetadataTag(tags, "White balance", metadata.WhiteBalance);
        AddMetadataTag(tags, "Lens make", metadata.LensMake);
        AddMetadataTag(tags, "Lens model", metadata.LensModel);
        AddMetadataTag(tags, "Digital zoom", metadata.DigitalZoom);
        return tags;
    }

    private static void AddMetadataTag(ICollection<MediaMetadataTag> tags, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            tags.Add(new MediaMetadataTag("Media", name, value));
        }
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ScanInfoTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ScanInfoMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsReady { get; set; }

    [ObservableProperty]
    public partial bool IsScanInfoOpen { get; set; }

    [ObservableProperty]
    public partial string ThumbnailStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoBarErrorTitle { get; set; } = "Error";

    [ObservableProperty]
    public partial string InfoBarErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsInfoBarOpen { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    public bool IsGeneratingThumbnails
    {
        get => _isGeneratingThumbnails;
        private set => SetProperty(ref _isGeneratingThumbnails, value);
    }

    public event Action<FolderNode?>? FolderRestored;

    public HashSet<Guid> ExpandedFolderIds { get; } = [];

    /// <summary>Opens the error InfoBar with the supplied message.</summary>
    public void ReportError(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        InfoBarErrorTitle = Settings.ErrorTitle;
        InfoBarErrorMessage = message;
        IsInfoBarOpen = true;
    }

    public async Task ClearFolderThumbnailCacheAsync(FolderNode folderNode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folderNode);

        var afterId = Guid.Empty;
        var clearedCount = 0;
        var failedFileCount = 0;
        string? cleanupError = null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await queries.SearchPageAsync(new MediaQuery(
                    FolderId: folderNode.Folder.Id,
                    SearchText: null,
                    MediaType: MediaTypeFilter.Any,
                    SortField: MediaSortField.Id,
                    Descending: false,
                    AfterId: afterId,
                    PageSize: 500), cancellationToken);

                foreach (var file in page.Items)
                {
                    if (file.ThumbnailPath is not null)
                    {
                        await mediaFiles.UpdateThumbnailPathAsync(file.Id, null, cancellationToken);
                        SetLoadedThumbnail(file.Id, null);
                        clearedCount++;
                    }

                    try
                    {
                        await thumbnailGenerator.ClearCacheAsync(file, cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        failedFileCount++;
                        cleanupError = exception.Message;
                        Log($"Could not remove thumbnail file for {file.Path}: {exception.Message}");
                    }
                }

                if (page.NextAfterId is not { } nextAfterId || nextAfterId == afterId)
                {
                    break;
                }

                afterId = nextAfterId;
            }

            StatusText = failedFileCount == 0
                ? $"Cleared {clearedCount} thumbnail(s) for {folderNode.PathText}"
                : $"Cleared {clearedCount} thumbnail(s) for {folderNode.PathText}; could not delete files for {failedFileCount} item(s): {cleanupError}";
            if (failedFileCount > 0)
            {
                ReportError(StatusText);
            }

            Log(StatusText);
            RequestVisibleThumbnailRegeneration();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not clear thumbnail cache for {folderNode.PathText}: {exception.Message}";
            ReportError(StatusText);
            Log(StatusText);
        }
    }

    private void SetLoadedThumbnail(Guid mediaFileId, string? thumbnailPath)
    {
        var index = IndexOfLoadedItem(mediaFileId);
        if (index < 0)
        {
            return;
        }

        var item = Items[index];
        item.UpdateFile(item.File.WithThumbnailPath(thumbnailPath));
    }

    private void SetThumbnailState(Guid mediaFileId, ThumbnailLoadState state)
    {
        var index = IndexOfLoadedItem(mediaFileId);
        if (index >= 0)
        {
            Items[index].SetThumbnailState(state);
        }
    }

    private int IndexOfLoadedItem(Guid mediaFileId)
    {
        for (var index = 0; index < Items.Count; index++)
        {
            if (Items[index].File.Id == mediaFileId)
            {
                return index;
            }
        }

        return -1;
    }

    public void ShowInitialLoading()
    {
        ScanInfoTitle = Localize("InitialLoadingTitle");
        ScanInfoMessage = Localize("InitialLoadingDatabase");
        IsScanInfoOpen = true;
    }

    public async Task ReportInitialLoadingAsync(string resourceKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ScanInfoMessage = Localize(resourceKey);
        Log($"Startup phase: {resourceKey}");
        await Dispatcher.UIThread.InvokeAsync(cancellationToken.ThrowIfCancellationRequested, DispatcherPriority.Background);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (!_xnfoSubscribed)
        {
            xnfoIndex.IndexingError += OnXnfoIndexingError;
            xnfoIndex.MetadataChanged += OnXnfoMetadataChanged;
            _xnfoSubscribed = true;
            if (xnfoIndex.LastError is { } error)
                ReportError(error);
        }
        indexer.AutomaticScanStarted += OnAutomaticScanStarted;
        indexer.AutomaticScanProgress += OnAutomaticScanProgress;
        indexer.AutomaticScanFinished += OnAutomaticScanFinished;
        indexer.IndexingIssue += OnIndexingIssue;
        indexer.ItemsSkipped += OnItemsSkipped;
        Log("Initialize started");
        _isInitializing = true;
        var appSettings = settingsSession.Current;
        Log($"Settings loaded: SelectedFolderId={appSettings.SelectedFolderId?.ToString() ?? "null"} expanded={appSettings.ExpandedFolderIds.Count}");
        ExpandedFolderIds.Clear();
        ExpandedFolderIds.UnionWith(appSettings.ExpandedFolderIds);

        IsDetailsPaneOpen = appSettings.IsDetailsPaneOpen;
        ShowFileNames = appSettings.ShowFileNames;
        ShowFileTypeBadges = appSettings.ShowFileTypeBadges;
        SortFilesByFolder = appSettings.SortFilesByFolder;
        IncludeDescendants = appSettings.IncludeDescendants;
        IncludeImages = appSettings.IncludeImages;
        IncludeVideos = appSettings.IncludeVideos;
        IncludeAudio = appSettings.IncludeAudio;
        ThumbnailSize = Math.Clamp(appSettings.ThumbnailSize, MinThumbnailSize, MaxThumbnailSize);
        LeftPaneWidth = Math.Clamp(appSettings.LeftPaneWidth, MinLeftPaneWidth, MaxLeftPaneWidth);
        await ReportInitialLoadingAsync("InitialLoadingFolders", cancellationToken);
        await LoadFolderTreeAsync(cancellationToken);

        await ReportInitialLoadingAsync("InitialLoadingTree", cancellationToken);
        FolderNode? selectedNode = null;
        if (appSettings.SelectedFolderId is { } savedFolderId)
        {
            selectedNode = FindFolderNode(FolderTree, savedFolderId);
            Log(selectedNode is null
                ? $"Saved folder {savedFolderId} not found in tree"
                : $"Restoring selected folder {savedFolderId}");
        }

        selectedNode ??= FolderTree.FirstOrDefault();
        _suppressSelectedFolderReload = true;
        try
        {
            SelectedFolder = selectedNode;
        }
        finally
        {
            _suppressSelectedFolderReload = false;
        }

        FolderRestored?.Invoke(selectedNode);
        await Dispatcher.UIThread.InvokeAsync(cancellationToken.ThrowIfCancellationRequested, DispatcherPriority.Background);
        await ReloadAsync(_selectedFolderId, cancellationToken);

        Log("Initialize completed");
        _isInitializing = false;
        UpdateSettingsSession();
        IsReady = true;
    }

    public void OnVisibleItemsChanged(IReadOnlyList<MediaFile> visibleFiles)
    {
        var currentVisibleFiles = visibleFiles.ToArray();
        lock (_thumbnailRegenerationLock)
        {
            if (!HaveSameMediaFiles(_visibleThumbnailFiles, currentVisibleFiles))
            {
                _visibleThumbnailVersion++;
            }

            _visibleThumbnailFiles = currentVisibleFiles;
        }

        if (!_isInitializing)
        {
            StartVisibleThumbnailRegenerationWorker();
        }
    }

    private static bool HaveSameMediaFiles(IReadOnlyList<MediaFile> left, IReadOnlyList<MediaFile> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].Id != right[index].Id)
            {
                return false;
            }
        }

        return true;
    }

    private void RequestVisibleThumbnailRegeneration()
    {
        lock (_thumbnailRegenerationLock)
        {
            _visibleThumbnailVersion++;
        }

        StartVisibleThumbnailRegenerationWorker();
    }

    private void StartVisibleThumbnailRegenerationWorker()
    {
        lock (_thumbnailRegenerationLock)
        {
            if (_isInitializing || _thumbnailRegenerationWorkerRunning)
            {
                return;
            }

            _thumbnailRegenerationWorkerRunning = true;
            //IsGeneratingThumbnails = true;
        }

        _ = RegenerateVisibleThumbnailsAsync();
    }
    /*
    private async Task RegenerateVisibleThumbnailsAsync()
    {
        long observedVersion = 0;

        try
        {
            while (true)
            {
                IReadOnlyList<MediaFile> visibleFiles;
                int thumbnailSize;
                lock (_thumbnailRegenerationLock)
                {
                    observedVersion = _visibleThumbnailVersion;
                    visibleFiles = _visibleThumbnailFiles;
                    var baseThumbnailSize = ThumbnailSize <= LargeThumbnailSizeThreshold
                        ? SmallThumbnailSize
                        : LargeThumbnailSize;
                    thumbnailSize = (int)Math.Round(baseThumbnailSize * _renderScaling);
                }

                await Task.Delay(ThumbnailRegenerationDelayMilliseconds);
                if (!IsVisibleThumbnailVersionCurrent(observedVersion))
                {
                    continue;
                }

                var staleFiles = visibleFiles
                    .Where(file => file.ThumbnailPath is null ||
                        !File.Exists(file.ThumbnailPath) ||
                        !file.ThumbnailPath.EndsWith($"{thumbnailSize}.jpg", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (staleFiles.Length == 0)
                {
                    if (IsVisibleThumbnailVersionCurrent(observedVersion))
                    {
                        StatusText = "Visible thumbnails are up to date";
                        break;
                    }

                    continue;
                }

                await GenerateThumbnailsAsync(
                    staleFiles,
                    "Generating",
                    (file, _) => thumbnailGenerator.GenerateAsync(file, thumbnailSize, CancellationToken.None),
                    CancellationToken.None,
                    () => IsVisibleThumbnailVersionCurrent(observedVersion));

                if (IsVisibleThumbnailVersionCurrent(observedVersion))
                {
                    StatusText = "Finished generating visible thumbnails";
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            StatusText = $"Thumbnail regeneration failed: {exception.Message}";
            ReportError(StatusText);
            Log(StatusText);
        }
        finally
        {
            var restartWorker = false;
            lock (_thumbnailRegenerationLock)
            {
                _thumbnailRegenerationWorkerRunning = false;
                if (!_isInitializing && _visibleThumbnailVersion != observedVersion)
                {
                    _thumbnailRegenerationWorkerRunning = true;
                    restartWorker = true;
                }
                else
                {
                    IsGeneratingThumbnails = false;
                }
            }

            if (restartWorker)
            {
                _ = RegenerateVisibleThumbnailsAsync();
            }
        }
    }
    */
    private async Task RegenerateVisibleThumbnailsAsync()
    {
        long observedVersion = 0;

        try
        {
            while (true)
            {
                IReadOnlyList<MediaFile> visibleFiles;
                int thumbnailSize;
                lock (_thumbnailRegenerationLock)
                {
                    observedVersion = _visibleThumbnailVersion;
                    visibleFiles = _visibleThumbnailFiles;
                    var baseThumbnailSize = ThumbnailSize <= LargeThumbnailSizeThreshold
                        ? SmallThumbnailSize
                        : LargeThumbnailSize;
                    thumbnailSize = (int)Math.Round(baseThumbnailSize * _renderScaling);
                }

                // Debounce: waiting here is not "generating", so the flag stays false.
                await Task.Delay(ThumbnailRegenerationDelayMilliseconds);
                if (!IsVisibleThumbnailVersionCurrent(observedVersion))
                {
                    continue;
                }

                var suffix = $"{thumbnailSize}.jpg";
                var staleFiles = new List<MediaFile>(visibleFiles.Count);
                foreach (var file in visibleFiles)
                {
                    var folder = file.FolderId is { } folderId ? FindFolderNode(FolderTree, folderId) : null;
                    if (folder?.IsAvailable == false)
                        continue;
                    var thumbnailPath = file.ThumbnailPath;
                    if (thumbnailPath is null ||
                        !thumbnailPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(thumbnailPath))
                    {
                        if (folder is not null && !await Task.Run(() => File.Exists(file.Path)))
                            continue;
                        if (thumbnailGenerator.HasFailed(file))
                        {
                            SetThumbnailState(file.Id, ThumbnailLoadState.Failed);
                        }
                        else
                        {
                            staleFiles.Add(file);
                        }
                    }
                }

                if (staleFiles.Count == 0)
                {
                    if (IsVisibleThumbnailVersionCurrent(observedVersion))
                    {
                        ThumbnailStatusText = string.Empty;
                        break;
                    }

                    continue;
                }

                // Only now is work actually happening.
                IsGeneratingThumbnails = true;
                try
                {
                    await GenerateThumbnailsAsync(
                        staleFiles,
                        "Generating",
                        (file, _) => thumbnailGenerator.GenerateAsync(file, thumbnailSize, CancellationToken.None),
                        CancellationToken.None,
                        () => IsVisibleThumbnailVersionCurrent(observedVersion));
                    ThumbnailStatusText = string.Empty;
                }
                finally
                {
                    IsGeneratingThumbnails = false;
                }

                if (IsVisibleThumbnailVersionCurrent(observedVersion))
                {
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            ThumbnailStatusText = $"Thumbnail regeneration failed: {exception.Message}";
            ReportError(ThumbnailStatusText);
            Log(ThumbnailStatusText);
        }
        finally
        {
            var restartWorker = false;
            lock (_thumbnailRegenerationLock)
            {
                _thumbnailRegenerationWorkerRunning = false;
                if (!_isInitializing && _visibleThumbnailVersion != observedVersion)
                {
                    _thumbnailRegenerationWorkerRunning = true;
                    restartWorker = true;
                }
            }

            // Covers the exit paths where generation never started.
            IsGeneratingThumbnails = false;

            if (restartWorker)
            {
                _ = RegenerateVisibleThumbnailsAsync();
            }
        }
    }

    private bool IsVisibleThumbnailVersionCurrent(long version)
    {
        /*
        lock (_thumbnailRegenerationLock)
        {
            return _visibleThumbnailVersion == version;
        }
        */
        // Interlocked.Read gives an atomic 64-bit read on 32-bit as well,
        // without taking the lock on every check.
        return Interlocked.Read(ref _visibleThumbnailVersion) == version;
    }

    private async Task<int> GenerateThumbnailsAsync(
        IReadOnlyList<MediaFile> files,
        string progressVerb,
        Func<MediaFile, CancellationToken, Task<string?>> generate,
        CancellationToken cancellationToken,
        Func<bool>? shouldContinue = null)
    {
        var generated = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (shouldContinue is not null && !shouldContinue())
            {
                break;
            }
            if (file.FolderId is { } folderId && FindFolderNode(FolderTree, folderId)?.IsAvailable == false)
                continue;

            ThumbnailStatusText = $"Updating thumbnails: {progressVerb} {file.FileName}";
            SetThumbnailState(file.Id, ThumbnailLoadState.Loading);

            string? thumbnailPath;
            try
            {
                thumbnailPath = await generate(file, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                SetThumbnailState(file.Id, ThumbnailLoadState.Pending);
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                SetThumbnailState(file.Id, ThumbnailLoadState.Failed);
                throw;
            }

            if (thumbnailPath is null)
            {
                SetThumbnailState(file.Id, ThumbnailLoadState.Failed);
                continue;
            }

            await mediaFiles.UpdateThumbnailPathAsync(file.Id, thumbnailPath, cancellationToken);
            SetLoadedThumbnail(file.Id, thumbnailPath);
            generated++;

            if (generated < files.Count && (shouldContinue is null || shouldContinue()))
            {
                await Task.Delay(ThumbnailRegenerationDelayMilliseconds, cancellationToken);
            }
        }

        return generated;
    }

    [RelayCommand]
    public async Task LoadFolderTreeAsync(CancellationToken cancellationToken)
    {
        var tree = await Task.Run(() => queries.GetFolderTreeAsync(cancellationToken), cancellationToken);
        ApplyFolderTree(tree);
    }

    private void ApplyFolderTree(IReadOnlyList<FolderNode> tree)
    {
        FolderTree.Clear();
        foreach (var node in tree)
        {
            FolderTree.Add(node);
        }

        Log($"Folder tree loaded: roots={FolderTree.Count}");
        StartFolderAvailabilityChecks();
    }

    private void StartFolderAvailabilityChecks()
    {
        _folderAvailabilityCts?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _folderAvailabilityCts = cancellation;
        var roots = FolderTree.ToArray();
        _ = CheckFolderAvailabilityAsync(roots, cancellation);
    }

    private async Task CheckFolderAvailabilityAsync(IReadOnlyList<FolderNode> roots, CancellationTokenSource cancellation)
    {
        try
        {
            async Task CheckNodeAsync(FolderNode node, bool parentAvailable, bool retryOnly)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var available = parentAvailable && !_offlineLibraryPaths.Contains(Path.TrimEndingDirectorySeparator(node.PathText)) &&
                    (retryOnly && node.IsAvailable == true ||
                    await Task.Run(() => Directory.Exists(node.PathText), cancellation.Token));
                cancellation.Token.ThrowIfCancellationRequested();
                var wasUnavailable = node.IsAvailable == false;
                node.IsAvailable = available;
                if (available && wasUnavailable)
                    RequestVisibleThumbnailRegeneration();
                foreach (var child in node.Children)
                    await CheckNodeAsync(child, available, retryOnly);
            }

            async Task CheckRootAsync(FolderNode root)
            {
                try
                {
                    var retryOnly = false;
                    while (true)
                    {
                        await CheckNodeAsync(root, true, retryOnly);
                        retryOnly = true;
                        await Task.Delay(TimeSpan.FromSeconds(30), cancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    if (!_disposed)
                        ReportError(string.Format(Localize("FolderAvailabilityError"), exception.Message));
                }
            }

            await Task.WhenAll(roots.Select(CheckRootAsync));
        }
        finally
        {
            if (ReferenceEquals(_folderAvailabilityCts, cancellation))
                _folderAvailabilityCts = null;
            cancellation.Dispose();
        }
    }

    public async Task<bool> IsMediaAvailableAsync(MediaFile file, CancellationToken cancellationToken)
    {
        var exists = await Task.Run(() => File.Exists(file.Path), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!exists)
        {
            StatusText = string.Format(Localize("MediaUnavailable"), file.FileName);
            var folder = file.FolderId is { } folderId ? FindFolderNode(FolderTree, folderId) : null;
            if (folder is not null && !await Task.Run(() => Directory.Exists(folder.PathText), cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (folder.IsAvailable != false)
                {
                    SetFolderAvailability(folder, false);
                    StartFolderAvailabilityChecks();
                }
            }
        }
        return exists;
    }

    private static void SetFolderAvailability(FolderNode node, bool available)
    {
        node.IsAvailable = available;
        if (!available)
            foreach (var child in node.Children)
                SetFolderAvailability(child, false);
    }

    private static FolderNode? FindFolderNode(IEnumerable<FolderNode> nodes, Guid folderId)
    {
        foreach (var node in nodes)
        {
            if (node.Folder.Id == folderId)
            {
                return node;
            }

            var child = FindFolderNode(node.Children, folderId);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static FolderNode? FindFolderNodeByPath(IEnumerable<FolderNode> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.PathText, path, StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            var child = FindFolderNodeByPath(node.Children, path);
            if (child is not null)
            {
                return child;
            }
        }

        return null;
    }

    private static void Log(string message)
    {
        Debug.WriteLine($"[MainViewModel {DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(_selectedFolderId, cancellationToken);
    }

    public async Task LoadExcludedFoldersAsync(CancellationToken cancellationToken)
    {
        try
        {
            var paths = await libraryCommands.GetExcludedFoldersAsync(cancellationToken);
            ExcludedFolders.Clear();
            foreach (var path in paths)
            {
                ExcludedFolders.Add(path);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Settings.StatusText = string.Format(Settings.ExcludedFoldersLoadError, exception.Message);
            ReportError(Settings.StatusText);
        }
    }

    public async Task RestoreExcludedFolderAsync(string path, CancellationToken cancellationToken)
    {
        if (!Settings.ScanSubfolders)
        {
            Settings.StatusText = Settings.RestoreFolderScanDisabled;
            return;
        }
        if (IsScanning || !IsReady)
        {
            Settings.StatusText = Localize("ScanBusy");
            return;
        }

        try
        {
            Settings.StatusText = string.Format(Settings.RestoreFolderInProgress, path);
            await RunScanAsync(path,
                (progress, token) => libraryCommands.RestoreExcludedFolderAsync(path, progress, token),
                token => RefreshTreeAndSelectionAsync(token), cancellationToken);
            Settings.StatusText = Settings.RestoreFolderSuccess;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Settings.StatusText = string.Format(Settings.RestoreFolderError, path, exception.Message);
            ReportError(Settings.StatusText);
        }
        finally
        {
            if (!_disposed)
                await LoadExcludedFoldersAsync(CancellationToken.None);
        }
    }

    public Task<bool> IsLibraryRootAsync(string path, CancellationToken cancellationToken) =>
        libraryCommands.IsLibraryRootAsync(path, cancellationToken);

    public async Task RescanLibraryAsync(string path, CancellationToken cancellationToken)
    {
        if (IsScanning || !IsReady)
        {
            ReportError(Localize("ScanBusy"));
            return;
        }

        var localizer = new ResourceLocalizer(CultureInfo.GetCultureInfo(settingsSession.Current.Language == "ja" ? "ja" : "en"));
        try
        {
            await RunScanAsync(path,
                (progress, token) => libraryCommands.RescanLibraryByRootAsync(path, progress, token),
                token => RefreshTreeAndSelectionAsync(token), cancellationToken);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            StatusText = string.Format(localizer["RescanLibraryError"], path, exception.Message);
            ReportError(StatusText);
        }
    }

    public async Task AddFolderAsync(string path, CancellationToken cancellationToken)
    {
        if (IsScanning || !IsReady)
        {
            ReportError(Localize("ScanBusy"));
            return;
        }

        await RunScanAsync(path, async (progress, token) =>
        {
            Guid id;
            try
            {
                id = await libraryCommands.AddLibraryAsync(path, token).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                await libraryCommands.RescanLibraryByRootAsync(path, progress, token).ConfigureAwait(false);
                return;
            }

            await libraryCommands.RefreshLibraryAsync(id, progress, token).ConfigureAwait(false);
        }, token => ReloadLibraryRootAsync(path, token), cancellationToken);
    }

    private async Task ReloadLibraryRootAsync(string path, CancellationToken cancellationToken)
    {
        var existingTree = FolderTree.ToArray();
        var (tree, unchanged) = await Task.Run(async () =>
        {
            var updated = await queries.GetFolderTreeAsync(cancellationToken).ConfigureAwait(false);
            return (updated, FolderTreesEqual(existingTree, updated));
        }, cancellationToken);
        var previousFolderId = _selectedFolderId;
        if (!unchanged)
        {
            _suppressSelectedFolderReload = true;
            try
            {
                ApplyFolderTree(tree);
            }
            finally
            {
                _suppressSelectedFolderReload = false;
            }
        }
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = FolderTree.FirstOrDefault(node =>
            string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(node.Folder.Path)),
                fullPath,
                StringComparison.OrdinalIgnoreCase));

        if (root is not null)
        {
            _suppressSelectedFolderReload = true;
            try
            {
                SelectedFolder = root;
            }
            finally
            {
                _suppressSelectedFolderReload = false;
            }

            if (!unchanged || previousFolderId != root.Folder.Id)
                FolderRestored?.Invoke(root);
        }

        if (previousFolderId == _selectedFolderId)
            await RefreshMediaAsync(cancellationToken);
        else
            await ReloadAsync(_selectedFolderId, cancellationToken);
    }

    private async Task RefreshTreeAndSelectionAsync(CancellationToken cancellationToken)
    {
        var existingTree = FolderTree.ToArray();
        var (tree, unchanged) = await Task.Run(async () =>
        {
            var updated = await queries.GetFolderTreeAsync(cancellationToken).ConfigureAwait(false);
            return (updated, FolderTreesEqual(existingTree, updated));
        }, cancellationToken);
        if (!unchanged)
        {
            var selectedId = _selectedFolderId;
            _suppressSelectedFolderReload = true;
            FolderNode? selectedNode;
            try
            {
                ApplyFolderTree(tree);
                selectedNode = selectedId is { } id ? FindFolderNode(FolderTree, id) : null;
                SelectedFolder = selectedNode;
            }
            finally
            {
                _suppressSelectedFolderReload = false;
            }
            FolderRestored?.Invoke(selectedNode);
        }
        await RefreshMediaAsync(cancellationToken);
    }

    private static bool FolderTreesEqual(IReadOnlyList<FolderNode> left, IReadOnlyList<FolderNode> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            if (a.Folder.Id != b.Folder.Id || a.Folder.ParentId != b.Folder.ParentId ||
                a.Folder.Path != b.Folder.Path || a.IsRoot != b.IsRoot || !FolderTreesEqual(a.Children, b.Children))
                return false;
        }
        return true;
    }

    private async Task RunScanAsync(string path, Func<IProgress<ScanProgress>, CancellationToken, Task> scan, Func<CancellationToken, Task> refresh, CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        var token = linkedCancellation.Token;
        var version = ++_scanVersion;
        _activeScanOperations++;
        IsScanning = true;
        IsScanInfoOpen = true;
        ScanInfoTitle = Localize("ScanTitle");
        ScanInfoMessage = string.Format(Localize("ScanStarting"), path);
        var acceptingProgress = true;
        var progress = new Progress<ScanProgress>(value =>
        {
            if (acceptingProgress && !_disposed && version == _scanVersion && IsScanning)
                ReportScanProgress(value, path);
        });
        try
        {
            await Task.Run(() => scan(progress, token), token);
            acceptingProgress = false;
            if (_offlineLibraryPaths.Remove(Path.TrimEndingDirectorySeparator(path)))
                StartFolderAvailabilityChecks();
            if (version == _scanVersion)
            {
                ScanInfoTitle = Localize("ScanRefreshingTitle");
                ScanInfoMessage = string.Format(Localize("ScanRefreshing"), path);
            }
            await RunScanRefreshAsync(() => refresh(token), token);
        }
        finally
        {
            _activeScanOperations--;
            IsScanning = _activeScanOperations > 0;
            if (!IsScanning)
                IsScanInfoOpen = false;
        }
    }

    private async Task RunScanRefreshAsync(Func<Task> refresh, CancellationToken cancellationToken)
    {
        await _scanRefreshSemaphore.WaitAsync(cancellationToken);
        try
        {
            await refresh();
        }
        finally
        {
            _scanRefreshSemaphore.Release();
        }
    }

    private string Localize(string key) =>
        new ResourceLocalizer(CultureInfo.GetCultureInfo(settingsSession.Current.Language == "ja" ? "ja" : "en"))[key];

    private void ReportScanProgress(ScanProgress progress, string path)
    {
        ScanInfoTitle = Localize(progress.Phase);
        ScanInfoMessage = progress.Total > 0
            ? string.Format(Localize("ScanCount"), path, progress.Processed, progress.Total)
            : progress.Processed > 0
                ? string.Format(Localize("ScanFoundCount"), path, progress.Processed)
                : path;
    }

    private void OnAutomaticScanStarted(string path) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        ++_scanVersion;
        _activeScanOperations++;
        _automaticScanPath = path;
        IsScanning = true;
        IsScanInfoOpen = true;
        ScanInfoTitle = Localize("ScanTitle");
        ScanInfoMessage = string.Format(Localize("ScanStarting"), path);
    });

    private void OnAutomaticScanProgress(ScanProgress progress) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed && IsScanning)
            ReportScanProgress(progress, _automaticScanPath);
    });

    private string _automaticScanPath = string.Empty;

    private void OnAutomaticScanFinished(string path, bool isOnline, Exception? error) => Dispatcher.UIThread.Post(async () =>
    {
        if (_disposed) return;
        try
        {
            if (error is not null)
            {
                if (error is not OperationCanceledException)
                    ReportError(string.Format(Localize("ScanFailed"), path, error.Message));
                return;
            }
            if (!isOnline)
            {
                _offlineLibraryPaths.Add(Path.TrimEndingDirectorySeparator(path));
                var offlineNode = FindFolderNodeByPath(FolderTree, path);
                if (offlineNode is not null)
                    SetFolderAvailability(offlineNode, false);
                StartFolderAvailabilityChecks();
                ReportError(string.Format(Localize("ScanOfflineMessage"), path));
                return;
            }
            _offlineLibraryPaths.Remove(Path.TrimEndingDirectorySeparator(path));
            StartFolderAvailabilityChecks();
            ScanInfoTitle = Localize("ScanRefreshingTitle");
            ScanInfoMessage = string.Format(Localize("ScanRefreshing"), path);
            await RunScanRefreshAsync(() => RefreshTreeAndSelectionAsync(_lifetimeCts.Token), _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                ReportError(string.Format(Localize("ScanFailed"), path, exception.Message));
            }
        }
        finally
        {
            if (!_disposed)
            {
                _activeScanOperations--;
                IsScanning = _activeScanOperations > 0;
                if (!IsScanning)
                    IsScanInfoOpen = false;
            }
        }
    });

    partial void OnSearchTextChanged(string? value)
    {
        Log($"Search text changed: '{value}'");
        ScheduleReload(SearchDebounceMilliseconds);
    }

    partial void OnSelectedSearchFieldChanged(SearchFieldOption value)
    {
        if (!_isInitializing && !string.IsNullOrWhiteSpace(SearchText))
        {
            ScheduleReload(0);
        }
    }

    partial void OnSelectedFolderChanged(FolderNode? value)
    {
        _selectedFolderId = value?.Folder.Id;
        Log($"Selection changed: folderId={_selectedFolderId?.ToString() ?? "none"}");
        SelectedFolderBreadcrumbs = BuildBreadcrumbs(value?.PathText);
        OnPropertyChanged(nameof(SelectedFolderBreadcrumbs));
        UpdateSettingsSession();
        if (!_suppressSelectedFolderReload)
        {
            ScheduleReload(0);
        }
    }

    private void OnIndexingIssue(string path, string message) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed)
            ReportError(string.Format(Localize("ScanFailed"), path, message));
    });

    private void OnItemsSkipped(string path, int count, string example) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed)
            ReportError(string.Format(Localize("ScanSkippedItems"), path, count, example));
    });

    private void OnXnfoIndexingError(string message) => Dispatcher.UIThread.Post(() => ReportError(message));

    private void OnXnfoMetadataChanged() => Dispatcher.UIThread.Post(() => ScheduleReload(SearchDebounceMilliseconds));

    private void ScheduleReload(int delayMilliseconds)
    {
        _reloadCts?.Cancel();
        _reloadCts = new CancellationTokenSource();
        _ = ReloadAfterDelayAsync(delayMilliseconds, _reloadCts.Token);
    }

    private async Task ReloadAfterDelayAsync(int delayMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delayMilliseconds, cancellationToken);
            await ReloadAsync(_selectedFolderId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Log("Reload cancelled");
        }
        catch (Exception exception)
        {
            StatusText = $"Failed to load media: {exception.Message}";
            ReportError(StatusText);
        }
    }

    public void SetFolderExpanded(Guid folderId, bool isExpanded)
    {
        var changed = isExpanded ? ExpandedFolderIds.Add(folderId) : ExpandedFolderIds.Remove(folderId);
        if (changed)
        {
            Log($"Folder {folderId} expanded={isExpanded}");
            UpdateSettingsSession();
        }
    }

    private void UpdateSettingsSession()
    {
        if (_isInitializing)
        {
            return;
        }

        var appSettings = settingsSession.Current;
        appSettings.SelectedFolderId = _selectedFolderId;
        appSettings.ExpandedFolderIds = ExpandedFolderIds.OrderBy(id => id).ToList();
        appSettings.IsDetailsPaneOpen = IsDetailsPaneOpen;
        appSettings.ShowFileNames = ShowFileNames;
        appSettings.ShowFileTypeBadges = ShowFileTypeBadges;
        appSettings.SortFilesByFolder = SortFilesByFolder;
        appSettings.IncludeDescendants = IncludeDescendants;
        appSettings.IncludeImages = IncludeImages;
        appSettings.IncludeVideos = IncludeVideos;
        appSettings.IncludeAudio = IncludeAudio;
        appSettings.ThumbnailSize = ThumbnailSize;
        appSettings.LeftPaneWidth = Math.Clamp(LeftPaneWidth, MinLeftPaneWidth, MaxLeftPaneWidth);
    }

    private async Task RefreshMediaAsync(CancellationToken cancellationToken)
    {
        await _mediaLoadSemaphore.WaitAsync(cancellationToken);
        try
        {
            var folderId = _selectedFolderId;
            if (folderId is null)
            {
                MediaCollectionSynchronizer.Apply(Items, []);
                SelectedItem = null;
                StatusText = "No library folder selected";
                return;
            }

            var query = CreateQuery(folderId);
            var sortByFolder = SortFilesByFolder;
            var originalThumbnails = Items.ToDictionary(item => item.File.Id, item => item.File.ThumbnailPath);
            var files = await Task.Run(async () =>
            {
                var result = await queries.SearchAllAsync(query, cancellationToken).ConfigureAwait(false);
                return MediaFileHierarchySorter.Sort(result, sortByFolder).ToArray();
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (_selectedFolderId != folderId || query != CreateQuery(_selectedFolderId) || sortByFolder != SortFilesByFolder)
                return;

            var currentThumbnails = Items.ToDictionary(item => item.File.Id, item => item.File.ThumbnailPath);
            for (var index = 0; index < files.Length; index++)
            {
                var file = files[index];
                if (originalThumbnails.TryGetValue(file.Id, out var originalPath) &&
                    currentThumbnails.TryGetValue(file.Id, out var currentPath) &&
                    originalPath != currentPath && file.ThumbnailPath == originalPath)
                {
                    files[index] = file.WithThumbnailPath(currentPath);
                }
            }

            var selected = SelectedItem;
            var selectedId = selected?.File.Id;
            var selectedFile = selected?.File;
            MediaCollectionSynchronizer.Apply(Items, files);
            if (selectedId is { } id)
            {
                var current = Items.FirstOrDefault(item => item.File.Id == id);
                if (!ReferenceEquals(SelectedItem, current))
                    SelectedItem = current;
                if (current is not null && ReferenceEquals(current, selected) && selectedFile is not null &&
                    (selectedFile.Path != current.File.Path || selectedFile.ModifiedDateUtc != current.File.ModifiedDateUtc || selectedFile.FileSize != current.File.FileSize))
                    await LoadSelectedMetadataAsync();
            }

            var currentById = Items.ToDictionary(item => item.File.Id, item => item.File);
            IReadOnlyList<MediaFile> visible;
            lock (_thumbnailRegenerationLock)
            {
                visible = _visibleThumbnailFiles.Where(file => currentById.ContainsKey(file.Id))
                    .Select(file => currentById[file.Id]).ToArray();
            }
            OnVisibleItemsChanged(visible);
            RequestVisibleThumbnailRegeneration();
            StatusText = $"{Items.Count} files, {CountFolders(folderId)} folders";
        }
        finally
        {
            _mediaLoadSemaphore.Release();
        }
    }

    private async Task ReloadAsync(Guid? folderId, CancellationToken cancellationToken)
    {
        await _mediaLoadSemaphore.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log($"Reload started: folderId={folderId?.ToString() ?? "none"} search='{SearchText}'");
            var selectedMediaId = SelectedItem?.File.Id;
            foreach (var item in Items)
            {
                item.Dispose();
            }

            _items.ReplaceAll([]);
            if (folderId is null)
            {
                StatusText = "No library folder selected";
                return;
            }

            var query = CreateQuery(folderId);
            if (_isInitializing)
                await ReportInitialLoadingAsync("InitialLoadingMedia", cancellationToken);
            var files = await Task.Run(() => queries.SearchAllAsync(query, cancellationToken), cancellationToken);
            var sortByFolder = SortFilesByFolder;
            if (_isInitializing)
                await ReportInitialLoadingAsync("InitialLoadingSorting", cancellationToken);
            var items = await Task.Run(() => MediaFileHierarchySorter.Sort(files, sortByFolder)
                .Select(file => new MediaItemViewModel(file))
                .ToArray(), cancellationToken);
            if (_isInitializing)
                await ReportInitialLoadingAsync("InitialLoadingDisplay", cancellationToken);
            _items.ReplaceAll(items);
            if (selectedMediaId is { } mediaId)
            {
                SelectedItem = items.FirstOrDefault(item => item.File.Id == mediaId);
            }

            var folderCount = CountFolders(folderId);
            StatusText = $"{Items.Count} files, {folderCount} folders";
        }
        finally
        {
            _mediaLoadSemaphore.Release();
        }
    }

    private int CountFolders(Guid? folderId)
    {
        if (folderId is null)
        {
            return CountDescendantFolders(FolderTree);
        }

        var node = FindFolderNode(FolderTree, folderId.Value);
        if (node is null)
        {
            return 0;
        }

        return IncludeDescendants ? CountDescendantFolders(node.Children) : node.Children.Count;
    }

    private static int CountDescendantFolders(IEnumerable<FolderNode> nodes) =>
        nodes.Sum(node => 1 + CountDescendantFolders(node.Children));

    private MediaQuery CreateQuery(Guid? folderId) => new(
        FolderId: folderId,
        SearchText: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
        MediaType: SelectedMediaTypeFilter,
        SortField: MediaSortField.Id,
        Descending: false,
        AfterId: Guid.Empty,
        PageSize: 0,
        IncludeDescendants: IncludeDescendants,
        SearchField: SelectedSearchField.Field);
}
