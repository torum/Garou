using Garou.Application.Events;
using Garou.Application.Libraries;
using Garou.Application.Queries;
using Garou.Domain.Abstractions;
using Garou.Infrastructure.Metadata;
using Garou.Infrastructure.Persistence;
using Garou.Infrastructure.Persistence.Migrations;
using Garou.Infrastructure.Persistence.Repositories;
using Garou.Infrastructure.Settings;
using Garou.Infrastructure.Sync;
using Garou.Infrastructure.Thumbnails;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace Garou.Composition;

public static class ServiceRegistration
{
    private const string FfmpegExecutable = "ffmpeg";
    private const string FfprobeExecutable = "ffprobe";
    private static readonly TimeSpan FfmpegTimeout = TimeSpan.FromSeconds(30);

    private static string ResolveExecutable(string name)
    {
        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var bundled = Path.Combine(AppContext.BaseDirectory, fileName);
        var resolved = File.Exists(bundled) ? bundled : name;
        Debug.WriteLine($"[ServiceRegistration] Resolved {name} to '{resolved}' (bundled exists: {File.Exists(bundled)})");
        return resolved;
    }

    public static ServiceProvider Build()
    {
        var appDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Garou");
        var thumbnailDirectory = Path.Combine(appDataDirectory, "Thumbnails");
        var databasePath = Path.Combine(appDataDirectory, "library.db");
        var settingsPath = Path.Combine(appDataDirectory, "settings.xml");

        Directory.CreateDirectory(thumbnailDirectory);

        var services = new ServiceCollection();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new SqliteConnectionFactory($"Data Source={databasePath}"));
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<DatabaseMaintenanceService>();
        services.AddSingleton<BatchWriter>();

        services.AddSingleton<IFolderRepository, FolderRepository>();
        services.AddSingleton<ITagRepository, TagRepository>();
        services.AddSingleton<IMediaFileRepository, MediaFileRepository>();
        services.AddSingleton<IThumbnailCacheRepository, ThumbnailCacheRepository>();
        services.AddSingleton<ILibraryRepository, LibraryRepository>();

        services.AddSingleton<MediaIndexerService>();
        services.AddSingleton<ILibraryScanner>(provider => provider.GetRequiredService<MediaIndexerService>());
        services.AddHostedService<MediaBackgroundService>();
        services.AddSingleton<LibraryCommands>();
        services.AddSingleton<IMediaSearchRepository, MediaFileRepository>();
        services.AddSingleton<MediaQueryService>();
        services.AddSingleton<IndexChangeEventBus>();
        services.AddTransient<Garou.ViewModels.MainViewModel>();
        services.AddTransient<Garou.ViewModels.SettingsViewModel>();

        services.AddSingleton<IFileWatcherService, FileWatcherService>();
        services.AddSingleton<IMediaMetadataProvider, ImageMetadataProvider>();
        services.AddSingleton<IMediaMetadataProvider>(_ => new VideoMetadataProvider(ResolveExecutable(FfprobeExecutable)));
        services.AddSingleton<IMetadataExtractionService, MetadataExtractionService>();

        services.AddSingleton(new FfmpegRunner(ResolveExecutable(FfmpegExecutable), FfmpegTimeout));
        services.AddSingleton<IThumbnailGeneratorService>(provider => new ThumbnailGeneratorService(
            provider.GetRequiredService<FfmpegRunner>(),
            provider.GetRequiredService<IThumbnailCacheRepository>(),
            thumbnailDirectory,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ThumbnailCacheEvictionService>();
        services.AddSingleton<ThumbnailEvictionScheduler>();

        services.AddSingleton(new SettingsStore(settingsPath));
        services.AddSingleton<AppSettingsSession>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }
}
