using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;

namespace Garou.Infrastructure.Thumbnails;

public sealed class ThumbnailGeneratorService(
    FfmpegRunner ffmpeg,
    IThumbnailCacheRepository cacheRepository,
    string thumbnailDirectory,
    TimeProvider timeProvider) : IThumbnailGeneratorService
{
    private const int ThumbnailSize = 320;
    private readonly ConcurrentDictionary<string, byte> _failedPaths = new(StringComparer.Ordinal);
    // Striped locks prevent concurrent attempts for the same file without retaining a lock per file.
    private readonly SemaphoreSlim[] _generationLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public bool HasFailed(MediaFile mediaFile) => _failedPaths.ContainsKey(mediaFile.Path);

    public async Task<string?> GenerateAsync(MediaFile mediaFile, CancellationToken cancellationToken)
    {
        return await GenerateAsync(mediaFile, ThumbnailSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GenerateAsync(MediaFile mediaFile, int thumbnailSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thumbnailSize);
        cancellationToken.ThrowIfCancellationRequested();

        if (mediaFile.MediaType is not (MediaType.Image or MediaType.Video) || HasFailed(mediaFile))
        {
            return null;
        }

        var generationLock = _generationLocks[(int)((uint)StringComparer.Ordinal.GetHashCode(mediaFile.Path) % (uint)_generationLocks.Length)];
        await generationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasFailed(mediaFile))
            {
                return null;
            }

            var sourceThumbnailDirectory = Path.Combine(thumbnailDirectory, ThumbnailFolderName(mediaFile.Path));
            var thumbnailPath = Path.Combine(sourceThumbnailDirectory, ThumbnailFileName(mediaFile.Path, thumbnailSize));
            if (File.Exists(thumbnailPath))
            {
                await RegisterThumbnailAsync(thumbnailPath, cancellationToken).ConfigureAwait(false);
                return thumbnailPath;
            }

            try
            {
                Directory.CreateDirectory(sourceThumbnailDirectory);
                var arguments = BuildArguments(mediaFile, thumbnailPath, thumbnailSize);
                var succeeded = await ffmpeg.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
                if (!succeeded || !File.Exists(thumbnailPath))
                {
                    Debug.WriteLine($"[ThumbnailGeneratorService] Generation failed for '{mediaFile.Path}'.");
                    _failedPaths.TryAdd(mediaFile.Path, 0);
                    File.Delete(thumbnailPath);
                    return null;
                }
            }
            catch (OperationCanceledException)
            {
                File.Delete(thumbnailPath);
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _failedPaths.TryAdd(mediaFile.Path, 0);
                throw;
            }

            await RegisterThumbnailAsync(thumbnailPath, cancellationToken).ConfigureAwait(false);
            return thumbnailPath;
        }
        finally
        {
            generationLock.Release();
        }
    }

    private Task RegisterThumbnailAsync(string thumbnailPath, CancellationToken cancellationToken) =>
        cacheRepository.UpsertAsync(
            new ThumbnailCacheEntry
            {
                ThumbnailPath = thumbnailPath,
                SizeBytes = new FileInfo(thumbnailPath).Length,
                LastAccessUtc = timeProvider.GetUtcNow().UtcDateTime
            },
            cancellationToken);

    public async Task ClearCacheAsync(MediaFile mediaFile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mediaFile);

        var thumbnailPrefix = ThumbnailFilePrefix(mediaFile.Path);
        var sourceThumbnailDirectory = Path.Combine(thumbnailDirectory, ThumbnailFolderName(mediaFile.Path));
        var thumbnailPaths = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(sourceThumbnailDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(sourceThumbnailDirectory, $"{thumbnailPrefix}*.jpg"))
            {
                thumbnailPaths.Add(path);
            }
        }

        if (mediaFile.ThumbnailPath is { } currentThumbnailPath)
        {
            thumbnailPaths.Add(currentThumbnailPath);
        }

        Exception? deletionFailure = null;
        foreach (var thumbnailPath in thumbnailPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await cacheRepository.DeleteAsync(thumbnailPath, cancellationToken).ConfigureAwait(false);
            try
            {
                if (File.Exists(thumbnailPath))
                {
                    File.Delete(thumbnailPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                deletionFailure ??= exception;
            }
        }

        if (deletionFailure is not null)
        {
            throw new IOException("One or more thumbnail files could not be deleted.", deletionFailure);
        }
    }

    internal static string ThumbnailFolderName(string filePath)
    {
        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath))
            ?? throw new ArgumentException("The media file path must have a parent directory.", nameof(filePath));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sourceDirectory));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static string ThumbnailFileName(string fullFilePath)
    {
        return ThumbnailFileName(fullFilePath, ThumbnailSize);
    }

    internal static string ThumbnailFileName(string fullFilePath, int thumbnailSize)
    {
        return $"{ThumbnailFilePrefix(fullFilePath)}{thumbnailSize}.jpg";
    }

    private static string ThumbnailFilePrefix(string fullFilePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullFilePath));
        return $"{Convert.ToHexString(hash).ToLowerInvariant()}-";
    }

    internal static IReadOnlyList<string> BuildArguments(MediaFile mediaFile, string thumbnailPath)
    {
        return BuildArguments(mediaFile, thumbnailPath, ThumbnailSize);
    }

    internal static IReadOnlyList<string> BuildArguments(MediaFile mediaFile, string thumbnailPath, int thumbnailSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thumbnailSize);
        var arguments = new List<string> { "-y" };

        if (mediaFile.MediaType == MediaType.Video)
        {
            arguments.AddRange(["-ss", "3"]);
        }

        arguments.AddRange(["-i", mediaFile.Path]);

        if (mediaFile.MediaType == MediaType.Video)
        {
            arguments.AddRange(["-frames:v", "1"]);
        }

        arguments.AddRange(["-vf", $"scale={thumbnailSize}:{thumbnailSize}:force_original_aspect_ratio=decrease:flags=lanczos", "-q:v", "2", thumbnailPath]);
        return arguments;
    }
}
