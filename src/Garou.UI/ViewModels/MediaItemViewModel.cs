using System.Collections.ObjectModel;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
using Garou.UI.Models;
using System.Globalization;

namespace Garou.ViewModels;

public sealed class MediaItemViewModel : IDisposable
{
    private static readonly string[] FileSizeUnits = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    public MediaItemViewModel(MediaFile file)
    {
        File = file;
        Thumbnail = file.ThumbnailPath is { } thumbnailPath && System.IO.File.Exists(thumbnailPath)
            ? new ThumbnailImage(thumbnailPath)
            : null;
    }

    public MediaFile File { get; }

    public ThumbnailImage? Thumbnail { get; }

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

    public void Dispose() => Thumbnail?.Dispose();
}
