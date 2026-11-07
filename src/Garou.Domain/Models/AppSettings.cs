namespace Garou.Domain.Models;

public sealed class AppSettings
{
    public string Language { get; set; } = "en";

    public long ThumbnailCacheLimitBytes { get; set; } = 900L * 1024 * 1024;

    public long? SelectedFolderId { get; set; }

    public List<long> ExpandedFolderIds { get; set; } = [];

    public bool IsDetailsPaneOpen { get; set; } = true;

    public double ThumbnailSize { get; set; } = 120;

    public double LeftPaneWidth { get; set; } = 260;

    public bool ShowFileNames { get; set; } = true;

    public bool ShowFileTypeBadges { get; set; } = true;

    public bool IncludeDescendants { get; set; } = true;

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public bool? WindowMaximized { get; set; }
}
