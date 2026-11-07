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

    public async Task<string?> GenerateAsync(MediaFile mediaFile, CancellationToken cancellationToken)
    {
        return await GenerateAsync(mediaFile, ThumbnailSize, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GenerateAsync(MediaFile mediaFile, int thumbnailSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thumbnailSize);

        if (mediaFile.MediaType is not (MediaType.Image or MediaType.Video))
        {
            return null;
        }

        var thumbnailPath = Path.Combine(thumbnailDirectory, ThumbnailFileName(mediaFile.Path, thumbnailSize));
        if (File.Exists(thumbnailPath))
        {
            await RegisterThumbnailAsync(thumbnailPath, cancellationToken).ConfigureAwait(false);
            return thumbnailPath;
        }

        Directory.CreateDirectory(thumbnailDirectory);

        var arguments = BuildArguments(mediaFile, thumbnailPath, thumbnailSize);
        var succeeded = await ffmpeg.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!succeeded || !File.Exists(thumbnailPath))
        {
            return null;
        }

        await RegisterThumbnailAsync(thumbnailPath, cancellationToken).ConfigureAwait(false);

        return thumbnailPath;
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
        var thumbnailPaths = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(thumbnailDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(thumbnailDirectory, $"{thumbnailPrefix}*.jpg"))
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
