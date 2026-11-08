using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
using Garou.UI.Models;
using System.Globalization;

namespace Garou.ViewModels;

public sealed class MediaItemViewModel : ObservableObject, IDisposable
{
    private static readonly string[] FileSizeUnits = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    private ThumbnailImage? _thumbnail;
    private ThumbnailLoadState _thumbnailState = ThumbnailLoadState.Pending;
    private bool _isDisposed;

    public MediaItemViewModel(MediaFile file)
    {
        File = file;
    }

    public MediaFile File { get; }

    public ThumbnailImage? Thumbnail
    {
        get
        {
            if (_thumbnail is null && !_isDisposed && File.ThumbnailPath is { } thumbnailPath && System.IO.File.Exists(thumbnailPath))
            {
                _thumbnail = new ThumbnailImage(thumbnailPath);
                _thumbnail.PropertyChanged += OnThumbnailPropertyChanged;
                SetThumbnailState(_thumbnail.State);
            }

            return _thumbnail;
        }
    }

    public ThumbnailLoadState ThumbnailState => _thumbnailState;

    public bool IsThumbnailPlaceholderVisible => ThumbnailState is ThumbnailLoadState.Pending or ThumbnailLoadState.Loading;

    public bool IsThumbnailImageVisible => ThumbnailState == ThumbnailLoadState.Loaded;

    public bool IsThumbnailFailureVisible => ThumbnailState == ThumbnailLoadState.Failed;

    internal void SetThumbnailState(ThumbnailLoadState state)
    {
        if (_isDisposed || !SetProperty(ref _thumbnailState, state, nameof(ThumbnailState)))
        {
            return;
        }

        OnPropertyChanged(nameof(IsThumbnailPlaceholderVisible));
        OnPropertyChanged(nameof(IsThumbnailImageVisible));
        OnPropertyChanged(nameof(IsThumbnailFailureVisible));
    }

    private void OnThumbnailPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ThumbnailImage.State) && sender is ThumbnailImage thumbnail)
        {
            SetThumbnailState(thumbnail.State);
        }
    }

    public ObservableCollection<MediaMetadataTag> DetailedTags { get; } = [];

    public bool HasDetailedMetadata => DetailedTags.Count > 0;

    internal void SetDetailedTags(IReadOnlyList<MediaMetadataTag> tags)
    {
        DetailedTags.Clear();
        foreach (var tag in tags)
        {
            DetailedTags.Add(tag);
        }
    }

    public string FileName => File.FileName;

    public MediaType MediaType => File.MediaType;

    public string MediaTypeText => MediaType.ToString();

    public bool ShowsDetailedMetadata => MediaType != MediaType.Unknown;

    public string IdText => File.Id.ToString("D");

    public string ExtensionText => FormatText(File.Extension);

    public string FileSizeText => File.FileSize is { } fileSize
        ? FormatFileSize(fileSize)
        : "—";

    public string HashText => FormatText(File.HashValue);

    public string CreatedDateText => FormatDate(File.CreatedDateUtc);

    public string ModifiedDateText => FormatDate(File.ModifiedDateUtc);

    public string LastIndexedDateText => FormatDate(File.LastIndexedUtc);

    public string FolderIdText => File.FolderId?.ToString("D") ?? "—";

    public string ThumbnailPathText => FormatText(File.ThumbnailPath);

    private static string FormatFileSize(long bytes)
    {
        double size = bytes;
        var unitIndex = 0;
        while (size >= 1024 && unitIndex < FileSizeUnits.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        var formattedSize = unitIndex == 0
            ? $"{size:N0} {FileSizeUnits[unitIndex]}"
            : $"{size:N1} {FileSizeUnits[unitIndex]}";

        return $"{formattedSize} ({bytes:N0} bytes)";
    }

    private static string FormatDate(DateTime? date) => date?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";

    private static string FormatText(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    public void Dispose()
    {
        _isDisposed = true;
        if (_thumbnail is not null)
        {
            _thumbnail.PropertyChanged -= OnThumbnailPropertyChanged;
        }

        _thumbnail?.Dispose();
        _thumbnail = null;
    }
}
