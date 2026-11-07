using System.Globalization;
using System.Xml.Linq;
using Garou.Domain.Models;

namespace Garou.Infrastructure.Settings;

public sealed class SettingsStore(string settingsFilePath)
{
    private readonly SemaphoreSlim _fileAccess = new(1, 1);

    public static string DefaultSettingsFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaOrganizer", "settings.xml");

    private static readonly string[] SupportedLanguages = ["en", "ja"];

    public static string DetectLanguage(CultureInfo uiCulture)
    {
        var twoLetter = uiCulture.TwoLetterISOLanguageName;
        return SupportedLanguages.Contains(twoLetter, StringComparer.OrdinalIgnoreCase) ? twoLetter : "en";
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        await _fileAccess.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(settingsFilePath))
            {
                return new AppSettings { Language = DetectLanguage(CultureInfo.CurrentUICulture) };
            }

            await using var stream = File.OpenRead(settingsFilePath);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            var root = document.Root;

            return new AppSettings
            {
                Language = root?.Element("Language")?.Value is { Length: > 0 } language
                    ? language
                    : DetectLanguage(CultureInfo.CurrentUICulture),
                ThumbnailCacheLimitBytes = long.TryParse(root?.Element("ThumbnailCacheLimitBytes")?.Value, out var limit)
                    ? limit
                    : new AppSettings().ThumbnailCacheLimitBytes,
                IncludedExtensions = ReadIncludedExtensions(root),
                SelectedFolderId = Guid.TryParse(root?.Element("SelectedFolderId")?.Value, out var folderId)
                    ? folderId
                    : null,
                ExpandedFolderIds = (root?.Element("ExpandedFolderIds")?.Value ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(id => Guid.TryParse(id, out var value) ? value : (Guid?)null)
                    .OfType<Guid>()
                    .ToList(),
                IsDetailsPaneOpen = bool.TryParse(root?.Element("IsDetailsPaneOpen")?.Value, out var isDetailsPaneOpen)
                    ? isDetailsPaneOpen
                    : true,
                ThumbnailSize = double.TryParse(root?.Element("ThumbnailSize")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var thumbnailSize)
                    ? Math.Clamp(thumbnailSize, 80, 320)
                    : new AppSettings().ThumbnailSize,
                LeftPaneWidth = double.TryParse(root?.Element("LeftPaneWidth")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var leftPaneWidth)
                    ? Math.Clamp(leftPaneWidth, 180, 500)
                    : new AppSettings().LeftPaneWidth,
                DetailsPaneWidth = double.TryParse(root?.Element("DetailsPaneWidth")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var detailsPaneWidth)
                    ? Math.Clamp(detailsPaneWidth, 180, 600)
                    : new AppSettings().DetailsPaneWidth,
                ShowFileNames = bool.TryParse(root?.Element("ShowFileNames")?.Value, out var showFileNames)
                    ? showFileNames
                    : new AppSettings().ShowFileNames,
                ShowFileTypeBadges = bool.TryParse(root?.Element("ShowFileTypeBadges")?.Value, out var showFileTypeBadges)
                    ? showFileTypeBadges
                    : new AppSettings().ShowFileTypeBadges,
                IncludeDescendants = bool.TryParse(root?.Element("IncludeDescendants")?.Value, out var includeDescendants)
                    ? includeDescendants
                    : new AppSettings().IncludeDescendants,
                ScanSubfolders = bool.TryParse(root?.Element("ScanSubfolders")?.Value, out var scanSubfolders)
                    ? scanSubfolders
                    : new AppSettings().ScanSubfolders,
                ImageViewerApplicationPath = root?.Element("ImageViewerApplicationPath")?.Value ?? string.Empty,
                ImageEditorApplicationPath = root?.Element("ImageEditorApplicationPath")?.Value ?? string.Empty,
                VideoViewerApplicationPath = root?.Element("VideoViewerApplicationPath")?.Value ?? string.Empty,
                VideoEditorApplicationPath = root?.Element("VideoEditorApplicationPath")?.Value ?? string.Empty,
                AudioViewerApplicationPath = root?.Element("AudioViewerApplicationPath")?.Value ?? string.Empty,
                AudioEditorApplicationPath = root?.Element("AudioEditorApplicationPath")?.Value ?? string.Empty,
                WindowWidth = double.TryParse(root?.Element("WindowWidth")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var windowWidth) && double.IsFinite(windowWidth)
                    ? windowWidth
                    : null,
                WindowHeight = double.TryParse(root?.Element("WindowHeight")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var windowHeight) && double.IsFinite(windowHeight)
                    ? windowHeight
                    : null,
                WindowX = int.TryParse(root?.Element("WindowX")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var windowX)
                    ? windowX
                    : null,
                WindowY = int.TryParse(root?.Element("WindowY")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var windowY)
                    ? windowY
                    : null,
                WindowMaximized = bool.TryParse(root?.Element("WindowMaximized")?.Value, out var windowMaximized)
                    ? windowMaximized
                    : null
            };
        }
        finally
        {
            _fileAccess.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _fileAccess.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(settingsFilePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var document = new XDocument(
                new XElement("Settings",
                    new XElement("Language", settings.Language),
                    new XElement("ThumbnailCacheLimitBytes", settings.ThumbnailCacheLimitBytes),
                    new XElement("IncludedExtensions", settings.IncludedExtensions.Select(extension => new XElement("Extension", extension))),
                    new XElement("SelectedFolderId", settings.SelectedFolderId?.ToString("N") ?? string.Empty),
                    new XElement("ExpandedFolderIds", string.Join(',', settings.ExpandedFolderIds.Select(id => id.ToString("N")))),
                    new XElement("IsDetailsPaneOpen", settings.IsDetailsPaneOpen.ToString(CultureInfo.InvariantCulture)),
                    new XElement("ThumbnailSize", settings.ThumbnailSize.ToString(CultureInfo.InvariantCulture)),
                    new XElement("LeftPaneWidth", settings.LeftPaneWidth.ToString(CultureInfo.InvariantCulture)),
                    new XElement("DetailsPaneWidth", settings.DetailsPaneWidth.ToString(CultureInfo.InvariantCulture)),
                    new XElement("ShowFileNames", settings.ShowFileNames.ToString(CultureInfo.InvariantCulture)),
                    new XElement("ShowFileTypeBadges", settings.ShowFileTypeBadges.ToString(CultureInfo.InvariantCulture)),
                    new XElement("IncludeDescendants", settings.IncludeDescendants.ToString(CultureInfo.InvariantCulture)),
                    new XElement("ScanSubfolders", settings.ScanSubfolders.ToString(CultureInfo.InvariantCulture)),
                    new XElement("ImageViewerApplicationPath", settings.ImageViewerApplicationPath),
                    new XElement("ImageEditorApplicationPath", settings.ImageEditorApplicationPath),
                    new XElement("VideoViewerApplicationPath", settings.VideoViewerApplicationPath),
                    new XElement("VideoEditorApplicationPath", settings.VideoEditorApplicationPath),
                    new XElement("AudioViewerApplicationPath", settings.AudioViewerApplicationPath),
                    new XElement("AudioEditorApplicationPath", settings.AudioEditorApplicationPath),
                    new XElement("WindowWidth", settings.WindowWidth?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                    new XElement("WindowHeight", settings.WindowHeight?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                    new XElement("WindowX", settings.WindowX?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                    new XElement("WindowY", settings.WindowY?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                    new XElement("WindowMaximized", settings.WindowMaximized?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)));

            await using var stream = File.Create(settingsFilePath);
            await document.SaveAsync(stream, SaveOptions.None, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileAccess.Release();
        }
    }

    private static List<string> ReadIncludedExtensions(XElement? root)
    {
        var storedExtensions = root?.Element("IncludedExtensions");
        if (storedExtensions is null)
        {
            return [.. MediaFileExtensions.All];
        }

        var extensions = storedExtensions.Elements("Extension")
            .Select(extension => extension.Value)
            .Where(extension => MediaFileExtensions.All.Contains(extension, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (extensions.Count == MediaFileExtensions.LegacyAll.Count
            && MediaFileExtensions.LegacyAll.All(extension => extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)))
        {
            return [.. MediaFileExtensions.All];
        }

        return extensions;
    }
}
