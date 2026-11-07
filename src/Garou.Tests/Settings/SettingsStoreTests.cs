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
        await File.WriteAllTextAsync(_settingsPath, "<Settings />");
        var store = new SettingsStore(_settingsPath);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.ShowFileTypeBadges);
    }

    [Fact]
    public async Task WhenDescendantPreferenceIsMissingThenItDefaultsToTrue()
    {
        await File.WriteAllTextAsync(_settingsPath, "<Settings />");
        var store = new SettingsStore(_settingsPath);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.IncludeDescendants);
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
