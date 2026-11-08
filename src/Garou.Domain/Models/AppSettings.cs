namespace Garou.Domain.Models;

public sealed class AppSettings
{
    public string Language { get; set; } = "en";

    public long ThumbnailCacheLimitBytes { get; set; } = 900L * 1024 * 1024;

    public List<string> IncludedExtensions { get; set; } = [.. MediaFileExtensions.All];

    public Guid? SelectedFolderId { get; set; }

    public List<Guid> ExpandedFolderIds { get; set; } = [];

    public bool IsDetailsPaneOpen { get; set; } = true;

    public double ThumbnailSize { get; set; } = 120;

    public double LeftPaneWidth { get; set; } = 260;

    public double DetailsPaneWidth { get; set; } = 280;

    public bool ShowFileNames { get; set; } = true;

    public bool ShowFileTypeBadges { get; set; } = true;

    public bool IncludeDescendants { get; set; } = true;

    public bool IncludeImages { get; set; } = true;

    public bool IncludeVideos { get; set; } = true;

    public bool IncludeAudio { get; set; } = true;

    public bool ScanSubfolders { get; set; } = true;

    public string ImageViewerApplicationPath { get; set; } = string.Empty;

    public string ImageEditorApplicationPath { get; set; } = string.Empty;

    public string VideoViewerApplicationPath { get; set; } = string.Empty;

    public string VideoEditorApplicationPath { get; set; } = string.Empty;

    public string AudioViewerApplicationPath { get; set; } = string.Empty;

    public string AudioEditorApplicationPath { get; set; } = string.Empty;

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    public int? WindowX { get; set; }

    public int? WindowY { get; set; }

    public bool? WindowMaximized { get; set; }
}
