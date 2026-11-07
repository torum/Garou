using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Garou.Application.Libraries;
using Garou.Application.Queries;
using Garou.Domain.Entities;
using Garou.Domain.Models;
using Garou.Domain.Abstractions;
using Garou.Infrastructure.Settings;

namespace Garou.ViewModels;

public partial class MainViewModel(
    MediaQueryService queries,
    LibraryCommands libraryCommands,
    SettingsViewModel settings,
    AppSettingsSession settingsSession,
    IThumbnailGeneratorService thumbnailGenerator,
    IMediaFileRepository mediaFiles,
    IFolderRepository folders) : ObservableObject, IDisposable
{
    private const int PageSize = 200;
    private const int SearchDebounceMilliseconds = 300;
    private const int ThumbnailRegenerationDelayMilliseconds = 300;
    private const double MinThumbnailSize = 80;
    private const double MaxThumbnailSize = 320;
    private const int SmallThumbnailWidth = 200;
    private const int LargeThumbnailWidth = 320;
    private const double LargeThumbnailSizeThreshold = 190;
    private double _renderScaling = 1;
    private const double MinLeftPaneWidth = 180;
    private const double MaxLeftPaneWidth = 500;
    private long _nextAfterId;
    private bool _hasMoreItems;
    private bool _isLoadingMore;
    private readonly SemaphoreSlim _mediaLoadSemaphore = new(1, 1);
    private long? _selectedFolderId;
    private CancellationTokenSource? _reloadCts;
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

    public ObservableCollection<MediaItemViewModel> Items => _items;

    public void Dispose()
    {
        foreach (var item in Items)
        {
            item.Dispose();
        }

        Items.Clear();
    }

    public ObservableCollection<FolderNode> FolderTree { get; } = [];

    public SettingsViewModel Settings { get; } = settings;

    [ObservableProperty]
    public partial bool IsDetailsPaneOpen { get; set; } = true;

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

        var folderIds = new HashSet<long>();
        CollectFolderIds(folderNode, folderIds);
        var afterId = 0L;
        var removedMediaCount = 0;

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
            StatusText = $"Removed {folderNode.PathText} and {removedMediaCount} indexed media item(s). Source files were not deleted; future scans may re-add this folder.";
            Log(StatusText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            StatusText = $"Could not remove {folderNode.PathText}: {exception.Message}";
            Log(StatusText);
        }
    }

    private static void CollectFolderIds(FolderNode node, HashSet<long> folderIds)
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
    public partial FolderNode? SelectedFolder { get; set; }

    [ObservableProperty]
    public partial MediaItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ScanStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double ScanProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    public bool IsGeneratingThumbnails
    {
        get => _isGeneratingThumbnails;
        private set => SetProperty(ref _isGeneratingThumbnails, value);
    }

    public event Action<FolderNode?>? FolderRestored;

    public HashSet<long> ExpandedFolderIds { get; } = [];

    public async Task ClearFolderThumbnailCacheAsync(FolderNode folderNode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folderNode);

        var afterId = 0L;
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
            Log(StatusText);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Could not clear thumbnail cache for {folderNode.PathText}: {exception.Message}";
            Log(StatusText);
        }
    }

    private void SetLoadedThumbnail(long mediaFileId, string? thumbnailPath)
    {
        var index = IndexOfLoadedItem(mediaFileId);
        if (index < 0)
        {
            return;
        }

        var previousItem = Items[index];
        var updatedItem = new MediaItemViewModel(previousItem.File.WithThumbnailPath(thumbnailPath));
        Items[index] = updatedItem;
        if (SelectedItem?.File.Id == mediaFileId)
        {
            SelectedItem = updatedItem;
        }

        previousItem.Dispose();
    }

    private int IndexOfLoadedItem(long mediaFileId)
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

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Log("Initialize started");
        _isInitializing = true;
        var appSettings = settingsSession.Current;
        Log($"Settings loaded: SelectedFolderId={appSettings.SelectedFolderId?.ToString() ?? "null"} expanded={appSettings.ExpandedFolderIds.Count}");
        ExpandedFolderIds.Clear();
        ExpandedFolderIds.UnionWith(appSettings.ExpandedFolderIds);

        IsDetailsPaneOpen = appSettings.IsDetailsPaneOpen;
        ShowFileNames = appSettings.ShowFileNames;
        ShowFileTypeBadges = appSettings.ShowFileTypeBadges;
        IncludeDescendants = appSettings.IncludeDescendants;
        ThumbnailSize = Math.Clamp(appSettings.ThumbnailSize, MinThumbnailSize, MaxThumbnailSize);
        LeftPaneWidth = Math.Clamp(appSettings.LeftPaneWidth, MinLeftPaneWidth, MaxLeftPaneWidth);
        await LoadFolderTreeAsync(cancellationToken);

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
        await ReloadAsync(_selectedFolderId, cancellationToken);

        Log("Initialize completed");
        _isInitializing = false;
        UpdateSettingsSession();
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
                int thumbnailWidth;
                lock (_thumbnailRegenerationLock)
                {
                    observedVersion = _visibleThumbnailVersion;
                    visibleFiles = _visibleThumbnailFiles;
                    var baseThumbnailWidth = ThumbnailSize <= LargeThumbnailSizeThreshold
                        ? SmallThumbnailWidth
                        : LargeThumbnailWidth;
                    thumbnailWidth = (int)Math.Round(baseThumbnailWidth * _renderScaling);
                }

                await Task.Delay(ThumbnailRegenerationDelayMilliseconds);
                if (!IsVisibleThumbnailVersionCurrent(observedVersion))
                {
                    continue;
                }

                var staleFiles = visibleFiles
                    .Where(file => file.ThumbnailPath is null ||
                        !File.Exists(file.ThumbnailPath) ||
                        !file.ThumbnailPath.EndsWith($"{thumbnailWidth}.jpg", StringComparison.OrdinalIgnoreCase))
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
                    (file, _) => thumbnailGenerator.GenerateAsync(file, thumbnailWidth, CancellationToken.None),
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
                int thumbnailWidth;
                lock (_thumbnailRegenerationLock)
                {
                    observedVersion = _visibleThumbnailVersion;
                    visibleFiles = _visibleThumbnailFiles;
                    var baseThumbnailWidth = ThumbnailSize <= LargeThumbnailSizeThreshold
                        ? SmallThumbnailWidth
                        : LargeThumbnailWidth;
                    thumbnailWidth = (int)Math.Round(baseThumbnailWidth * _renderScaling);
                }

                // Debounce: waiting here is not "generating", so the flag stays false.
                await Task.Delay(ThumbnailRegenerationDelayMilliseconds);
                if (!IsVisibleThumbnailVersionCurrent(observedVersion))
                {
                    continue;
                }

                var suffix = $"{thumbnailWidth}.jpg";
                var staleFiles = new List<MediaFile>(visibleFiles.Count);
                foreach (var file in visibleFiles)
                {
                    var thumbnailPath = file.ThumbnailPath;
                    if (thumbnailPath is null ||
                        !thumbnailPath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(thumbnailPath))
                    {
                        staleFiles.Add(file);
                    }
                }

                if (staleFiles.Count == 0)
                {
                    if (IsVisibleThumbnailVersionCurrent(observedVersion))
                    {
                        StatusText = "Visible thumbnails are up to date";
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
                        (file, _) => thumbnailGenerator.GenerateAsync(file, thumbnailWidth, CancellationToken.None),
                        CancellationToken.None,
                        () => IsVisibleThumbnailVersionCurrent(observedVersion));
                }
                finally
                {
                    IsGeneratingThumbnails = false;
                }

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

            StatusText = $"{progressVerb} thumbnail: {file.FileName}";
            var thumbnailPath = await generate(file, cancellationToken);
            if (thumbnailPath is not null)
            {
                await mediaFiles.UpdateThumbnailPathAsync(file.Id, thumbnailPath, cancellationToken);
                SetLoadedThumbnail(file.Id, thumbnailPath);
                generated++;

                if (generated < files.Count && (shouldContinue is null || shouldContinue()))
                {
                    await Task.Delay(ThumbnailRegenerationDelayMilliseconds, cancellationToken);
                }
            }
        }

        return generated;
    }

    [RelayCommand]
    public async Task LoadFolderTreeAsync(CancellationToken cancellationToken)
    {
        var tree = await queries.GetFolderTreeAsync(cancellationToken);
        FolderTree.Clear();
        foreach (var node in tree)
        {
            FolderTree.Add(node);
        }

        Log($"Folder tree loaded: roots={FolderTree.Count}");
    }

    private static FolderNode? FindFolderNode(IEnumerable<FolderNode> nodes, long folderId)
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

    private static void Log(string message)
    {
        Debug.WriteLine($"[MainViewModel {DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    [RelayCommand]
    public async Task SearchAsync(CancellationToken cancellationToken)
    {
        await ReloadAsync(_selectedFolderId, cancellationToken);
    }

    public async Task AddFolderAsync(string path, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var progress = new Progress<ScanProgress>(ReportScanProgress);
        try
        {
            await libraryCommands.AddLibraryAsync(id, path, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            StatusText = $"{path} is already a library. Rescanning...";
            await RunScanAsync(() => libraryCommands.RescanLibraryByRootAsync(path, progress, cancellationToken));
            await ReloadLibraryRootAsync(path, cancellationToken);
            return;
        }

        StatusText = $"Scanning {path}...";
        await RunScanAsync(() => libraryCommands.RefreshLibraryAsync(id, progress, cancellationToken));
        await ReloadLibraryRootAsync(path, cancellationToken);
    }

    private async Task ReloadLibraryRootAsync(string path, CancellationToken cancellationToken)
    {
        await LoadFolderTreeAsync(cancellationToken);
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

            FolderRestored?.Invoke(root);
        }

        await ReloadAsync(_selectedFolderId, cancellationToken);
    }

    private async Task RunScanAsync(Func<Task> scan)
    {
        IsScanning = true;
        try
        {
            await scan();
        }
        finally
        {
            IsScanning = false;
            ScanStatusText = string.Empty;
            ScanProgressPercent = 0;
        }
    }

    private void ReportScanProgress(ScanProgress progress)
    {
        ScanStatusText = progress.Total == 0
            ? progress.Phase
            : $"{progress.Phase}: {progress.Processed} / {progress.Total}";
        ScanProgressPercent = progress.Total == 0 ? 0 : 100.0 * progress.Processed / progress.Total;
    }

    partial void OnSearchTextChanged(string? value)
    {
        Log($"Search text changed: '{value}'");
        ScheduleReload(SearchDebounceMilliseconds);
    }

    partial void OnSelectedFolderChanged(FolderNode? value)
    {
        _selectedFolderId = value?.Folder.Id;
        Log($"Selection changed: folderId={_selectedFolderId?.ToString() ?? "none"}");
        UpdateSettingsSession();
        if (!_suppressSelectedFolderReload)
        {
            ScheduleReload(0);
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    public async Task LoadMoreAsync(CancellationToken cancellationToken)
    {
        await _mediaLoadSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!_hasMoreItems || _selectedFolderId is not { } folderId)
            {
                return;
            }

            _isLoadingMore = true;
            LoadMoreCommand.NotifyCanExecuteChanged();
            _hasMoreItems = await LoadNextPageAsync(folderId, cancellationToken);
        }
        finally
        {
            _isLoadingMore = false;
            _mediaLoadSemaphore.Release();
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanLoadMore() => _hasMoreItems && !_isLoadingMore;

    private void ScheduleReload(int delayMilliseconds)
    {
        _hasMoreItems = false;
        LoadMoreCommand.NotifyCanExecuteChanged();
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
        }
    }

    public void SetFolderExpanded(long folderId, bool isExpanded)
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
        appSettings.IncludeDescendants = IncludeDescendants;
        appSettings.ThumbnailSize = ThumbnailSize;
        appSettings.LeftPaneWidth = Math.Clamp(LeftPaneWidth, MinLeftPaneWidth, MaxLeftPaneWidth);
    }

    private async Task ReloadAsync(long? folderId, CancellationToken cancellationToken)
    {
        await _mediaLoadSemaphore.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Log($"Reload started: folderId={folderId?.ToString() ?? "none"} search='{SearchText}'");
            foreach (var item in Items)
            {
                item.Dispose();
            }

            _items.ReplaceAll([]);
            _nextAfterId = 0;
            _hasMoreItems = false;
            LoadMoreCommand.NotifyCanExecuteChanged();
            if (folderId is null)
            {
                StatusText = "No library folder selected";
                return;
            }

            var selectedFolder = FindFolderNode(FolderTree, folderId.Value);
            var shouldPaginate = IncludeDescendants && selectedFolder?.Children.Count > 0;
            var hasMore = await LoadNextPageAsync(folderId, cancellationToken);
            while (!shouldPaginate && hasMore)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasMore = await LoadNextPageAsync(folderId, cancellationToken);
            }

            _hasMoreItems = shouldPaginate && hasMore;
            LoadMoreCommand.NotifyCanExecuteChanged();

            var fileCount = await queries.CountAsync(CreateQuery(folderId, afterId: 0), cancellationToken);
            var folderCount = CountFolders(folderId);
            StatusText = $"{fileCount} files, {folderCount} folders";
        }
        finally
        {
            _mediaLoadSemaphore.Release();
            LoadMoreCommand.NotifyCanExecuteChanged();
        }
    }

    private int CountFolders(long? folderId)
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

    private MediaQuery CreateQuery(long? folderId, long afterId) => new(
        FolderId: folderId,
        SearchText: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
        MediaType: SelectedMediaTypeFilter,
        SortField: MediaSortField.Id,
        Descending: false,
        AfterId: afterId,
        PageSize: PageSize,
        IncludeDescendants: IncludeDescendants);

    private async Task<bool> LoadNextPageAsync(long? folderId, CancellationToken cancellationToken)
    {
        var page = await queries.SearchPageAsync(CreateQuery(folderId, _nextAfterId), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var newItems = page.Items
            .OrderBy(item => item.FileName, LogicalFileNameComparer.Instance)
            .Select(item => new MediaItemViewModel(item))
            .ToArray();
        var mergedItems = new List<MediaItemViewModel>(Items.Count + newItems.Length);
        var existingIndex = 0;
        var newIndex = 0;
        while (existingIndex < Items.Count && newIndex < newItems.Length)
        {
            if (LogicalFileNameComparer.Instance.Compare(Items[existingIndex].FileName, newItems[newIndex].FileName) <= 0)
            {
                mergedItems.Add(Items[existingIndex++]);
            }
            else
            {
                mergedItems.Add(newItems[newIndex++]);
            }
        }

        while (existingIndex < Items.Count)
        {
            mergedItems.Add(Items[existingIndex++]);
        }

        while (newIndex < newItems.Length)
        {
            mergedItems.Add(newItems[newIndex++]);
        }

        _items.ReplaceAll(mergedItems);
        Log($"Page loaded: returned={page.Items.Count} total={Items.Count} afterId={_nextAfterId} nextAfterId={page.NextAfterId?.ToString() ?? "none"}");
        _nextAfterId = page.NextAfterId ?? _nextAfterId;
        return page.Items.Count == PageSize;
    }

}
