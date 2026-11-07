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
    private const int ThumbnailWidth = 320;

    public async Task<string?> GenerateAsync(MediaFile mediaFile, CancellationToken cancellationToken)
    {
        return await GenerateAsync(mediaFile, ThumbnailWidth, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> GenerateAsync(MediaFile mediaFile, int thumbnailWidth, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thumbnailWidth);

        if (mediaFile.MediaType is not (MediaType.Image or MediaType.Video))
        {
            return null;
        }

        Directory.CreateDirectory(thumbnailDirectory);
        var thumbnailPath = Path.Combine(thumbnailDirectory, ThumbnailFileName(mediaFile.Path, thumbnailWidth));

        var arguments = BuildArguments(mediaFile, thumbnailPath, thumbnailWidth);
        var succeeded = await ffmpeg.RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!succeeded || !File.Exists(thumbnailPath))
        {
            return null;
        }

        await cacheRepository.UpsertAsync(
            new ThumbnailCacheEntry
            {
                ThumbnailPath = thumbnailPath,
                SizeBytes = new FileInfo(thumbnailPath).Length,
                LastAccessUtc = timeProvider.GetUtcNow().UtcDateTime
            },
            cancellationToken).ConfigureAwait(false);

        await RemoveSupersededThumbnailsAsync(ThumbnailFilePrefix(mediaFile.Path), thumbnailPath, cancellationToken)
            .ConfigureAwait(false);

        return thumbnailPath;
    }

    private async Task RemoveSupersededThumbnailsAsync(
        string thumbnailPrefix,
        string currentThumbnailPath,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(thumbnailDirectory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(thumbnailDirectory, $"{thumbnailPrefix}*.jpg"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(path, currentThumbnailPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            await cacheRepository.DeleteAsync(path, cancellationToken).ConfigureAwait(false);
        }
    }

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
        return ThumbnailFileName(fullFilePath, ThumbnailWidth);
    }

    internal static string ThumbnailFileName(string fullFilePath, int thumbnailWidth)
    {
        return $"{ThumbnailFilePrefix(fullFilePath)}{thumbnailWidth}.jpg";
    }

    private static string ThumbnailFilePrefix(string fullFilePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(fullFilePath));
        return $"{Convert.ToHexString(hash).ToLowerInvariant()}-";
    }

    internal static IReadOnlyList<string> BuildArguments(MediaFile mediaFile, string thumbnailPath)
    {
        return BuildArguments(mediaFile, thumbnailPath, ThumbnailWidth);
    }

    internal static IReadOnlyList<string> BuildArguments(MediaFile mediaFile, string thumbnailPath, int thumbnailWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thumbnailWidth);
        var arguments = new List<string> { "-y" };

        if (mediaFile.MediaType == MediaType.Video)
        {
            var seekSeconds = mediaFile.DurationSeconds is > 0
                ? mediaFile.DurationSeconds.Value * 0.1
                : 1.0;
            arguments.AddRange(["-ss", seekSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)]);
        }

        arguments.AddRange(["-i", mediaFile.Path]);

        if (mediaFile.MediaType == MediaType.Video)
        {
            arguments.AddRange(["-frames:v", "1"]);
        }

        arguments.AddRange(["-vf", $"scale={thumbnailWidth}:{thumbnailWidth}:force_original_aspect_ratio=decrease:flags=lanczos", "-q:v", "2", thumbnailPath]);
        return arguments;
    }
}
