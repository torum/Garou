using System.Globalization;
using Garou.Domain.Models;
using Garou.Infrastructure.Settings;
using Garou.Localization;

namespace Garou.Tests.Settings;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), $"Garou-settings-{Guid.NewGuid():N}.xml");

    public void Dispose()
    {
        if (File.Exists(_settingsPath))
        {
            File.Delete(_settingsPath);
        }
    }

    [Fact]
    public async Task WhenSettingsSavedThenLoadReturnsSameValues()
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings { Language = "ja", ThumbnailCacheLimitBytes = 123 }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("ja", loaded.Language);
    }

    [Fact]
    public async Task WhenSettingsSavedThenThumbnailLimitIsPreserved()
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings { ThumbnailCacheLimitBytes = 123 }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(123L, loaded.ThumbnailCacheLimitBytes);
    }

    [Theory]
    [InlineData("Default")]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task WhenThemePreferenceIsSavedThenItIsRestored(string themePreference)
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings { ThemePreference = themePreference }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(themePreference, loaded.ThemePreference);
    }

    [Fact]
    public async Task WhenThemePreferenceIsInvalidThenSystemDefaultIsUsed()
    {
        await File.WriteAllTextAsync(_settingsPath, "<Settings><ThemePreference>Unknown</ThemePreference></Settings>", TestContext.Current.CancellationToken);
        var store = new SettingsStore(_settingsPath);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal("Default", loaded.ThemePreference);
    }

    [Fact]
    public async Task WhenIncludedExtensionsAreSavedThenTheyAreRestored()
    {
        var extensions = new List<string> { ".mp4", ".png" };
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings { IncludedExtensions = extensions }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(extensions, loaded.IncludedExtensions);
    }

    [Fact]
    public async Task WhenIncludedExtensionsAreMissingThenAllSupportedExtensionsAreEnabled()
    {
        await File.WriteAllTextAsync(_settingsPath, "<Settings />", TestContext.Current.CancellationToken);
        var store = new SettingsStore(_settingsPath);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(MediaFileExtensions.All, loaded.IncludedExtensions);
    }

    [Fact]
    public async Task WhenLegacyDefaultExtensionListIsLoadedThenExpandedCatalogIsEnabled()
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings { IncludedExtensions = [.. MediaFileExtensions.LegacyAll] }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(MediaFileExtensions.All, loaded.IncludedExtensions);
    }

    [Fact]
    public void WhenBroadExtensionCatalogIsReadThenPlayableFormatsAreIncludedAndPlaylistsOrDiscImagesAreExcluded()
    {
        Assert.Contains(".flv", MediaFileExtensions.Videos);
        Assert.Contains(".avif", MediaFileExtensions.Images);
        Assert.Contains(".opus", MediaFileExtensions.Audio);
        Assert.DoesNotContain(".m3u8", MediaFileExtensions.All);
        Assert.DoesNotContain(".iso", MediaFileExtensions.All);
        Assert.DoesNotContain(".vob", MediaFileExtensions.All);
    }

    [Fact]
    public async Task WhenFolderSelectionIsSavedThenUuidIsPreserved()
    {
        var selectedFolderId = Guid.CreateVersion7();
        var expandedFolderId = Guid.CreateVersion7();
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings
        {
            SelectedFolderId = selectedFolderId,
            ExpandedFolderIds = [expandedFolderId]
        }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(selectedFolderId, loaded.SelectedFolderId);
        Assert.Equal(expandedFolderId, Assert.Single(loaded.ExpandedFolderIds));
    }

    [Fact]
    public async Task WhenMaximumThumbnailSizeIsSavedThenItIsRestored()
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings { ThumbnailSize = 320 }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(320, loaded.ThumbnailSize);
    }

    [Fact]
    public async Task WhenLeftPaneWidthIsSavedThenItIsRestored()
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings { LeftPaneWidth = 412.5 }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(412.5, loaded.LeftPaneWidth);
    }

    [Fact]
    public async Task WhenDetailsPaneWidthIsSavedThenItIsRestored()
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings { DetailsPaneWidth = 412.5 }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(412.5, loaded.DetailsPaneWidth);
    }

    [Fact]
    public async Task WhenWindowPlacementIsSavedThenItIsRestored()
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings
        {
            WindowWidth = 1280,
            WindowHeight = 800,
            WindowX = -1200,
            WindowY = 80,
            WindowMaximized = true
        }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(1280, loaded.WindowWidth);
        Assert.Equal(800, loaded.WindowHeight);
        Assert.Equal(-1200, loaded.WindowX);
        Assert.Equal(80, loaded.WindowY);
        Assert.True(loaded.WindowMaximized);
    }

    [Fact]
    public async Task WhenDescendantPreferenceIsSavedThenItIsPreserved()
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings { IncludeDescendants = false }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.IncludeDescendants);
    }

    [Fact]
    public async Task WhenFileTypeBadgePreferenceIsSavedThenItIsPreserved()
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings { ShowFileTypeBadges = false }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.ShowFileTypeBadges);
    }

    [Fact]
    public async Task WhenFileTypeBadgePreferenceIsMissingThenItDefaultsToTrue()
    {
        await File.WriteAllTextAsync(_settingsPath, "<Settings />", TestContext.Current.CancellationToken);
        var store = new SettingsStore(_settingsPath);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.ShowFileTypeBadges);
    }

    [Fact]
    public async Task WhenDescendantPreferenceIsMissingThenItDefaultsToTrue()
    {
        await File.WriteAllTextAsync(_settingsPath, "<Settings />", TestContext.Current.CancellationToken);
        var store = new SettingsStore(_settingsPath);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.IncludeDescendants);
    }

    [Fact]
    public async Task WhenScanSubfoldersPreferenceIsSavedThenItIsRestored()
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings { ScanSubfolders = false }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.ScanSubfolders);
    }

    [Fact]
    public async Task WhenMediaTypeApplicationPathsAreSavedThenTheyAreRestored()
    {
        var store = new SettingsStore(_settingsPath);
        await store.SaveAsync(new AppSettings
        {
            ImageViewerApplicationPath = @"C:\Apps\ImageViewer.exe",
            ImageEditorApplicationPath = @"C:\Apps\ImageEditor.exe",
            VideoViewerApplicationPath = @"C:\Apps\VideoViewer.exe",
            VideoEditorApplicationPath = @"C:\Apps\VideoEditor.exe",
            AudioViewerApplicationPath = @"C:\Apps\AudioViewer.exe",
            AudioEditorApplicationPath = @"C:\Apps\AudioEditor.exe"
        }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(@"C:\Apps\ImageViewer.exe", loaded.ImageViewerApplicationPath);
        Assert.Equal(@"C:\Apps\ImageEditor.exe", loaded.ImageEditorApplicationPath);
        Assert.Equal(@"C:\Apps\VideoViewer.exe", loaded.VideoViewerApplicationPath);
        Assert.Equal(@"C:\Apps\VideoEditor.exe", loaded.VideoEditorApplicationPath);
        Assert.Equal(@"C:\Apps\AudioViewer.exe", loaded.AudioViewerApplicationPath);
        Assert.Equal(@"C:\Apps\AudioEditor.exe", loaded.AudioEditorApplicationPath);
    }

    [Fact]
    public async Task WhenGlobalApplicationPathsAreLoadedThenMediaTypePathsRemainEmpty()
    {
        await File.WriteAllTextAsync(_settingsPath,
            "<Settings><ViewerApplicationPath>viewer.exe</ViewerApplicationPath><EditorApplicationPath>editor.exe</EditorApplicationPath></Settings>",
            TestContext.Current.CancellationToken);
        var store = new SettingsStore(_settingsPath);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Empty(loaded.ImageViewerApplicationPath);
        Assert.Empty(loaded.ImageEditorApplicationPath);
        Assert.Empty(loaded.VideoViewerApplicationPath);
        Assert.Empty(loaded.VideoEditorApplicationPath);
        Assert.Empty(loaded.AudioViewerApplicationPath);
        Assert.Empty(loaded.AudioEditorApplicationPath);
    }

    [Fact]
    public async Task WhenScanSubfoldersPreferenceIsMissingThenItDefaultsToTrue()
    {
        await File.WriteAllTextAsync(_settingsPath, "<Settings />", TestContext.Current.CancellationToken);
        var store = new SettingsStore(_settingsPath);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.ScanSubfolders);
    }

    [Fact]
    public async Task WhenSettingsAreSavedConcurrentlyThenFileAccessIsSerialized()
    {
        var store = new SettingsStore(_settingsPath);
        var saves = Enumerable.Range(0, 20)
            .Select(index => store.SaveAsync(new AppSettings { Language = index.ToString(CultureInfo.InvariantCulture) }, CancellationToken.None));

        await Task.WhenAll(saves);

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.NotEmpty(loaded.Language);
    }

    [Fact]
    public void WhenUiCultureIsJapaneseThenDetectedLanguageIsJa()
    {
        Assert.Equal("ja", SettingsStore.DetectLanguage(new CultureInfo("ja-JP")));
    }

    [Fact]
    public void WhenUiCultureIsUnsupportedThenDetectedLanguageFallsBackToEn()
    {
        Assert.Equal("en", SettingsStore.DetectLanguage(new CultureInfo("fr-FR")));
    }

    [Fact]
    public void WhenResourceLookedUpInJapaneseThenJapaneseTextIsReturned()
    {
        var localizer = new ResourceLocalizer(new CultureInfo("ja"));

        Assert.Equal("設定", localizer["SettingsTitle"]);
    }

    [Fact]
    public void WhenResourceLookedUpInEnglishThenEnglishTextIsReturned()
    {
        var localizer = new ResourceLocalizer(new CultureInfo("en"));

        Assert.Equal("Settings", localizer["SettingsTitle"]);
    }

    [Fact]
    public void WhenResourceKeyMissingThenKeyIsReturned()
    {
        var localizer = new ResourceLocalizer(new CultureInfo("en"));

        Assert.Equal("NoSuchKey", localizer["NoSuchKey"]);
    }
}
