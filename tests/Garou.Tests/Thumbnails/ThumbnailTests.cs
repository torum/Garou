using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;
using Garou.Domain.Models;
using Garou.Infrastructure.Settings;
using Garou.Infrastructure.Thumbnails;

namespace Garou.Tests.Thumbnails;

public sealed class ThumbnailTests
{
    [Fact]
    public async Task WhenFfmpegIsMissingThenGenerateReturnsNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Garou-thumbs-{Guid.NewGuid():N}");
        var repository = new FakeCacheRepository();
        var generator = new ThumbnailGeneratorService(
            new FfmpegRunner(Path.Combine(Path.GetTempPath(), $"missing-ffmpeg-{Guid.NewGuid():N}"), TimeSpan.FromSeconds(5)),
            repository,
            directory,
            TimeProvider.System);

        try
        {
            var result = await generator.GenerateAsync(
                new MediaFile { Path = "photo.jpg", FileName = "photo.jpg", Extension = ".jpg", MediaType = MediaType.Image },
                CancellationToken.None);

            Assert.Null(result);
            Assert.Empty(repository.Upserted);
            Assert.True(Directory.Exists(Path.Combine(directory, ThumbnailGeneratorService.ThumbnailFolderName("photo.jpg"))));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FailedGenerationIsSkippedForAllSizesUntilANewSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"Garou-thumbs-{Guid.NewGuid():N}");
        var ffmpeg = new FfmpegRunner(Path.Combine(Path.GetTempPath(), $"missing-ffmpeg-{Guid.NewGuid():N}"), TimeSpan.FromSeconds(5));
        var repository = new FakeCacheRepository();
        var generator = new ThumbnailGeneratorService(ffmpeg, repository, directory, TimeProvider.System);
        var media = new MediaFile { Path = "photo.jpg", FileName = "photo.jpg", Extension = ".jpg", MediaType = MediaType.Image };
        var other = new MediaFile { Path = "other.jpg", FileName = "other.jpg", Extension = ".jpg", MediaType = MediaType.Image };
        var sourceDirectory = Path.Combine(directory, ThumbnailGeneratorService.ThumbnailFolderName(media.Path));

        try
        {
            Assert.Null(await generator.GenerateAsync(media, 200, cancellationToken));
            Assert.True(generator.HasFailed(media));
            Assert.False(generator.HasFailed(other));
            Directory.Delete(sourceDirectory);

            await generator.ClearCacheAsync(media, cancellationToken);
            Assert.Null(await generator.GenerateAsync(media, 320, cancellationToken));
            Assert.False(Directory.Exists(sourceDirectory));
            Assert.Empty(repository.Upserted);

            Assert.Null(await generator.GenerateAsync(other, 200, cancellationToken));
            Assert.True(Directory.Exists(sourceDirectory));
            Directory.Delete(sourceDirectory);

            var newSession = new ThumbnailGeneratorService(ffmpeg, repository, directory, TimeProvider.System);
            Assert.False(newSession.HasFailed(media));
            Assert.Null(await newSession.GenerateAsync(media, 200, cancellationToken));
            Assert.True(Directory.Exists(sourceDirectory));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task GenerationExceptionIsReportedOnceAndNotRetried()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.GetTempFileName();
        var media = new MediaFile { Path = "photo.jpg", FileName = "photo.jpg", Extension = ".jpg", MediaType = MediaType.Image };
        var generator = new ThumbnailGeneratorService(new FfmpegRunner("ffmpeg", TimeSpan.FromSeconds(5)), new FakeCacheRepository(), directory, TimeProvider.System);

        try
        {
            await Assert.ThrowsAnyAsync<IOException>(() => generator.GenerateAsync(media, cancellationToken));
            Assert.True(generator.HasFailed(media));
            File.Delete(directory);
            Assert.Null(await generator.GenerateAsync(media, cancellationToken));
            Assert.False(Directory.Exists(directory));
        }
        finally
        {
            if (File.Exists(directory))
                File.Delete(directory);
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationDoesNotMarkGenerationAsFailed()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Garou-thumbs-{Guid.NewGuid():N}");
        var media = new MediaFile { Path = "photo.jpg", FileName = "photo.jpg", Extension = ".jpg", MediaType = MediaType.Image };
        var generator = new ThumbnailGeneratorService(new FfmpegRunner("ffmpeg", TimeSpan.FromSeconds(5)), new FakeCacheRepository(), directory, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync(media, cancellation.Token));

        Assert.False(generator.HasFailed(media));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void WhenSizeIsSpecifiedThenArgumentsScaleToRequestedSize()
    {
        var media = new MediaFile { Path = "photo.jpg", FileName = "photo.jpg", Extension = ".jpg", MediaType = MediaType.Image };

        var arguments = ThumbnailGeneratorService.BuildArguments(media, "thumbnail.jpg", 280);

        Assert.Contains("scale=280:280:force_original_aspect_ratio=decrease:flags=lanczos", arguments);
        Assert.Equal("-q:v", arguments[^3]);
        Assert.Equal("2", arguments[^2]);
    }

    [Fact]
    public void WhenMediaIsVideoThenArgumentsSeekToThreeSeconds()
    {
        var media = new MediaFile { Path = "clip.mp4", FileName = "clip.mp4", Extension = ".mp4", MediaType = MediaType.Video };

        var arguments = ThumbnailGeneratorService.BuildArguments(media, "thumbnail.jpg");

        Assert.Equal("3", arguments[2]);
    }

    [Fact]
    public void WhenSizeDiffersThenThumbnailCacheNamesDiffer()
    {
        var standard = ThumbnailGeneratorService.ThumbnailFileName("photo.jpg", 320);
        var highResolution = ThumbnailGeneratorService.ThumbnailFileName("photo.jpg", 280);

        Assert.NotEqual(standard, highResolution);
    }

    [Fact]
    public void WhenSourceFoldersDifferThenThumbnailFoldersDiffer()
    {
        var sourceFolder = Path.Combine(Path.GetTempPath(), "photos");
        var otherFolder = Path.Combine(Path.GetTempPath(), "other", "photos");
        var firstFolderHash = ThumbnailGeneratorService.ThumbnailFolderName(Path.Combine(sourceFolder, "first.jpg"));
        var secondFolderHash = ThumbnailGeneratorService.ThumbnailFolderName(Path.Combine(sourceFolder, "second.jpg"));
        var otherFolderHash = ThumbnailGeneratorService.ThumbnailFolderName(Path.Combine(otherFolder, "first.jpg"));

        Assert.Equal(firstFolderHash, secondFolderHash);
        Assert.NotEqual(firstFolderHash, otherFolderHash);
        Assert.Matches("^[0-9a-f]{64}$", firstFolderHash);
    }

    [Fact]
    public async Task WhenRequestedSizeIsCachedThenExistingVariantIsReused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"Garou-thumbs-{Guid.NewGuid():N}");
        var sourcePath = "photo.jpg";
        var sourceThumbnailDirectory = Path.Combine(directory, ThumbnailGeneratorService.ThumbnailFolderName(sourcePath));
        Directory.CreateDirectory(sourceThumbnailDirectory);
        var requestedPath = Path.Combine(sourceThumbnailDirectory, ThumbnailGeneratorService.ThumbnailFileName(sourcePath, 200));
        var otherSizePath = Path.Combine(sourceThumbnailDirectory, ThumbnailGeneratorService.ThumbnailFileName(sourcePath, 320));
        await File.WriteAllBytesAsync(requestedPath, [1], cancellationToken);
        await File.WriteAllBytesAsync(otherSizePath, [1], cancellationToken);
        var repository = new FakeCacheRepository();
        var generator = new ThumbnailGeneratorService(
            new FfmpegRunner(Path.Combine(Path.GetTempPath(), $"missing-ffmpeg-{Guid.NewGuid():N}"), TimeSpan.FromSeconds(5)),
            repository,
            directory,
            TimeProvider.System);

        try
        {
            var result = await generator.GenerateAsync(
                new MediaFile { Path = sourcePath, FileName = "photo.jpg", Extension = ".jpg", MediaType = MediaType.Image },
                200,
                cancellationToken);

            Assert.Equal(requestedPath, result);
            Assert.True(File.Exists(otherSizePath));
            Assert.Single(repository.Upserted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WhenMediaCacheIsClearedThenAllThumbnailSizesAreRemoved()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"Garou-thumbs-{Guid.NewGuid():N}");
        var sourcePath = "photo.jpg";
        var sourceThumbnailDirectory = Path.Combine(directory, ThumbnailGeneratorService.ThumbnailFolderName(sourcePath));
        Directory.CreateDirectory(sourceThumbnailDirectory);
        var repository = new FakeCacheRepository();
        var mediaFile = new MediaFile
        {
            Path = sourcePath,
            FileName = "photo.jpg",
            Extension = ".jpg",
            MediaType = MediaType.Image,
            ThumbnailPath = Path.Combine(sourceThumbnailDirectory, ThumbnailGeneratorService.ThumbnailFileName(sourcePath, 280))
        };
        var thumbnailPaths = new[]
        {
            mediaFile.ThumbnailPath,
            Path.Combine(sourceThumbnailDirectory, ThumbnailGeneratorService.ThumbnailFileName(sourcePath, 320))
        };
        var unrelatedThumbnailPath = Path.Combine(sourceThumbnailDirectory, ThumbnailGeneratorService.ThumbnailFileName("other.jpg", 320));
        var otherSourcePath = Path.Combine("other-folder", "photo.jpg");
        var otherSourceThumbnailDirectory = Path.Combine(directory, ThumbnailGeneratorService.ThumbnailFolderName(otherSourcePath));
        Directory.CreateDirectory(otherSourceThumbnailDirectory);
        var otherFolderThumbnailPath = Path.Combine(otherSourceThumbnailDirectory, ThumbnailGeneratorService.ThumbnailFileName(otherSourcePath, 320));

        foreach (var path in thumbnailPaths.Append(unrelatedThumbnailPath).Append(otherFolderThumbnailPath))
        {
            await File.WriteAllBytesAsync(path!, [1], cancellationToken);
            repository.Add(new ThumbnailCacheEntry { ThumbnailPath = path!, SizeBytes = 1, LastAccessUtc = DateTime.UtcNow });
        }

        var generator = new ThumbnailGeneratorService(
            new FfmpegRunner("ffmpeg", TimeSpan.FromSeconds(5)),
            repository,
            directory,
            TimeProvider.System);

        await generator.ClearCacheAsync(mediaFile, cancellationToken);

        Assert.All(thumbnailPaths, path => Assert.False(File.Exists(path)));
        Assert.Equal([unrelatedThumbnailPath, otherFolderThumbnailPath], repository.Remaining());
        Assert.True(File.Exists(unrelatedThumbnailPath));
        Assert.True(File.Exists(otherFolderThumbnailPath));
        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task WhenUnknownMediaTypeThenGenerateReturnsNull()
    {
        var repository = new FakeCacheRepository();
        var generator = new ThumbnailGeneratorService(
            new FfmpegRunner("ffmpeg", TimeSpan.FromSeconds(5)),
            repository,
            Path.GetTempPath(),
            TimeProvider.System);

        var result = await generator.GenerateAsync(
            new MediaFile { Path = "doc.txt", FileName = "doc.txt", Extension = ".txt", MediaType = MediaType.Unknown },
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task WhenCacheExceedsLimitThenLeastRecentlyAccessedEntriesAreEvicted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new FakeCacheRepository();
        var older = Path.Combine(Path.GetTempPath(), $"old-{Guid.NewGuid():N}.jpg");
        var newer = Path.Combine(Path.GetTempPath(), $"new-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(older, new byte[10], cancellationToken);
        await File.WriteAllBytesAsync(newer, new byte[10], cancellationToken);
        repository.Add(new ThumbnailCacheEntry { ThumbnailPath = older, SizeBytes = 10, LastAccessUtc = DateTime.UtcNow.AddDays(-1) });
        repository.Add(new ThumbnailCacheEntry { ThumbnailPath = newer, SizeBytes = 10, LastAccessUtc = DateTime.UtcNow });

        await new ThumbnailCacheEvictionService(repository).EvictAsync(10, CancellationToken.None);

        Assert.Equal([newer], repository.Remaining());
        Assert.False(File.Exists(older));
        File.Delete(newer);
    }

    [Fact]
    public async Task WhenLimitExceedsOneGigabyteThenItIsClampedBelowOneGigabyte()
    {
        var repository = new FakeCacheRepository();
        var path = Path.Combine(Path.GetTempPath(), $"clamp-{Guid.NewGuid():N}.jpg");
        repository.Add(new ThumbnailCacheEntry { ThumbnailPath = path, SizeBytes = 2L * 1024 * 1024 * 1024, LastAccessUtc = DateTime.UtcNow });

        await new ThumbnailCacheEvictionService(repository).EvictAsync(long.MaxValue, CancellationToken.None);

        Assert.Empty(repository.Remaining());
    }

    [Fact]
    public async Task WhenSchedulerRunsOnceThenCacheIsEvictedToConfiguredLimit()
    {
        var repository = new FakeCacheRepository();
        var settingsSession = new AppSettingsSession();
        settingsSession.Initialize(new AppSettings { ThumbnailCacheLimitBytes = 10 });
        var path = Path.Combine(Path.GetTempPath(), $"scheduled-{Guid.NewGuid():N}.jpg");
        repository.Add(new ThumbnailCacheEntry { ThumbnailPath = path, SizeBytes = 20, LastAccessUtc = DateTime.UtcNow });
        var scheduler = new ThumbnailEvictionScheduler(new ThumbnailCacheEvictionService(repository), settingsSession, TimeProvider.System);

        await scheduler.EvictOnceAsync(CancellationToken.None);

        Assert.Empty(repository.Remaining());
    }

    [Fact]
    public async Task WhenPeriodicTickFiresThenCacheIsEvicted()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new FakeCacheRepository();
        var settingsSession = new AppSettingsSession();
        settingsSession.Initialize(new AppSettings { ThumbnailCacheLimitBytes = 10 });
        repository.Add(new ThumbnailCacheEntry { ThumbnailPath = "tick.jpg", SizeBytes = 20, LastAccessUtc = DateTime.UtcNow });
        var timeProvider = new ManualTimeProvider();
        var scheduler = new ThumbnailEvictionScheduler(new ThumbnailCacheEvictionService(repository), settingsSession, timeProvider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = scheduler.RunAsync(TimeSpan.FromMinutes(10), cancellation.Token);

        timeProvider.Tick();
        await repository.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.Empty(repository.Remaining());
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new ManualTimer();
        }

        public void Tick() => _callback!(_state);

        private sealed class ManualTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCacheRepository : IThumbnailCacheRepository
    {
        private readonly List<ThumbnailCacheEntry> _entries = [];

        public List<ThumbnailCacheEntry> Upserted { get; } = [];

        public TaskCompletionSource Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Add(ThumbnailCacheEntry entry) => _entries.Add(entry);

        public IReadOnlyList<string> Remaining() => _entries.Select(e => e.ThumbnailPath).ToList();

        public Task<ThumbnailCacheEntry?> GetAsync(string thumbnailPath, CancellationToken cancellationToken) =>
            Task.FromResult(_entries.FirstOrDefault(e => e.ThumbnailPath == thumbnailPath));

        public Task UpsertAsync(ThumbnailCacheEntry entry, CancellationToken cancellationToken)
        {
            Upserted.Add(entry);
            _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<long> GetTotalSizeBytesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_entries.Sum(e => e.SizeBytes));

        public Task<IReadOnlyList<ThumbnailCacheEntry>> GetLeastRecentlyAccessedAsync(int count, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ThumbnailCacheEntry>>(_entries.OrderBy(e => e.LastAccessUtc).Take(count).ToList());

        public Task DeleteAsync(string thumbnailPath, CancellationToken cancellationToken)
        {
            _entries.RemoveAll(e => e.ThumbnailPath == thumbnailPath);
            Deleted.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
