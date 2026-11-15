using Garou.Infrastructure;
using Garou.Infrastructure.Settings;

namespace Garou.Tests.Settings;

public sealed class AppStoragePathsTests
{
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "home");
    private static readonly string LocalAppData = Path.Combine(Path.GetTempPath(), "local-app-data");

    [Fact]
    public void WindowsUsesLocalApplicationData()
    {
        var paths = AppStoragePaths.Resolve(StoragePlatform.Windows, Home, LocalAppData, null, null, null);
        var appDirectory = Path.Combine(LocalAppData, "torum", "Garou");

        Assert.Equal(Path.Combine(appDirectory, "Garou.config"), paths.SettingsFilePath);
        Assert.Equal(Path.Combine(appDirectory, "library.db"), paths.DatabaseFilePath);
        Assert.Equal(Path.Combine(appDirectory, "Cache", "Thumbnails"), paths.ThumbnailDirectory);
    }

    [Fact]
    public void LinuxUsesXdgDirectories()
    {
        var config = Path.Combine(Home, "custom-config");
        var data = Path.Combine(Home, "custom-data");
        var cache = Path.Combine(Home, "custom-cache");
        var paths = AppStoragePaths.Resolve(StoragePlatform.Linux, Home, LocalAppData, config, data, cache);

        Assert.Equal(Path.Combine(config, "torum", "Garou", "Garou.config"), paths.SettingsFilePath);
        Assert.Equal(Path.Combine(data, "torum", "Garou", "library.db"), paths.DatabaseFilePath);
        Assert.Equal(Path.Combine(cache, "torum", "Garou", "Thumbnails"), paths.ThumbnailDirectory);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    public void LinuxUsesDefaultsForInvalidXdgDirectories(string? xdgPath)
    {
        var paths = AppStoragePaths.Resolve(StoragePlatform.Linux, Home, LocalAppData, xdgPath, xdgPath, xdgPath);

        Assert.Equal(Path.Combine(Home, ".config", "torum", "Garou", "Garou.config"), paths.SettingsFilePath);
        Assert.Equal(Path.Combine(Home, ".local", "share", "torum", "Garou", "library.db"), paths.DatabaseFilePath);
        Assert.Equal(Path.Combine(Home, ".cache", "torum", "Garou", "Thumbnails"), paths.ThumbnailDirectory);
    }

    [Fact]
    public void MacOSUsesLibraryDirectories()
    {
        var paths = AppStoragePaths.Resolve(StoragePlatform.MacOS, Home, LocalAppData, null, null, null);
        var appDirectory = Path.Combine(Home, "Library", "Application Support", "torum", "Garou");

        Assert.Equal(Path.Combine(appDirectory, "Garou.config"), paths.SettingsFilePath);
        Assert.Equal(Path.Combine(appDirectory, "library.db"), paths.DatabaseFilePath);
        Assert.Equal(Path.Combine(Home, "Library", "Caches", "torum", "Garou", "Thumbnails"), paths.ThumbnailDirectory);
    }

    [Fact]
    public void DefaultSettingsPathMatchesCurrentPlatform()
    {
        Assert.Equal(AppStoragePaths.SettingsFilePath, SettingsStore.DefaultSettingsFilePath);
    }

    [Fact]
    public void MissingRequiredRootIsReported()
    {
        Assert.Throws<InvalidOperationException>(() => AppStoragePaths.Resolve(StoragePlatform.Windows, Home, "", null, null, null));
        Assert.Throws<InvalidOperationException>(() => AppStoragePaths.Resolve(StoragePlatform.Linux, "", LocalAppData, null, null, null));
        Assert.Throws<InvalidOperationException>(() => AppStoragePaths.Resolve(StoragePlatform.MacOS, "", LocalAppData, null, null, null));
    }
}
