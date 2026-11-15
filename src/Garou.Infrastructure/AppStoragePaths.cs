namespace Garou.Infrastructure;

public static class AppStoragePaths
{
    private const string AppDeveloper = "torum";
    private const string AppName = "Garou";

    private static StoragePaths Current => Resolve(
        OperatingSystem.IsWindows() ? StoragePlatform.Windows :
        OperatingSystem.IsLinux() ? StoragePlatform.Linux :
        OperatingSystem.IsMacOS() ? StoragePlatform.MacOS :
        throw new PlatformNotSupportedException("Garou storage paths are not defined for this platform."),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME"));

    public static string SettingsFilePath => Current.SettingsFilePath;

    public static string DatabaseFilePath => Current.DatabaseFilePath;

    public static string ThumbnailDirectory => Current.ThumbnailDirectory;

    internal static StoragePaths Resolve(StoragePlatform platform, string home, string localAppData,
        string? xdgConfigHome, string? xdgDataHome, string? xdgCacheHome)
    {
        string configRoot;
        string dataRoot;
        string cacheRoot;
        if (platform == StoragePlatform.Windows)
        {
            if (string.IsNullOrWhiteSpace(localAppData))
                throw new InvalidOperationException("The local application data directory is unavailable.");

            configRoot = dataRoot = localAppData;
            cacheRoot = Path.Combine(localAppData, AppDeveloper, AppName, "Cache");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(home))
                throw new InvalidOperationException("The user profile directory is unavailable.");

            if (platform == StoragePlatform.Linux)
            {
                configRoot = XdgRoot(xdgConfigHome, Path.Combine(home, ".config"));
                dataRoot = XdgRoot(xdgDataHome, Path.Combine(home, ".local", "share"));
                cacheRoot = Path.Combine(XdgRoot(xdgCacheHome, Path.Combine(home, ".cache")), AppDeveloper, AppName);
            }
            else if (platform == StoragePlatform.MacOS)
            {
                configRoot = dataRoot = Path.Combine(home, "Library", "Application Support");
                cacheRoot = Path.Combine(home, "Library", "Caches", AppDeveloper, AppName);
            }
            else
            {
                throw new PlatformNotSupportedException("Garou storage paths are not defined for this platform.");
            }
        }

        return new StoragePaths(
            Path.Combine(configRoot, AppDeveloper, AppName, "Garou.config"),
            Path.Combine(dataRoot, AppDeveloper, AppName, "library.db"),
            Path.Combine(cacheRoot, "Thumbnails"));
    }

    private static string XdgRoot(string? value, string fallback) =>
        !string.IsNullOrEmpty(value) && Path.IsPathFullyQualified(value) ? value : fallback;
}

internal enum StoragePlatform { Windows, Linux, MacOS }

internal readonly record struct StoragePaths(string SettingsFilePath, string DatabaseFilePath, string ThumbnailDirectory);
