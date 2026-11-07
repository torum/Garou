using System.Diagnostics;
using Garou.Domain.Abstractions;
using Garou.Domain.Models;

namespace Garou.Infrastructure.Metadata;

public sealed class VideoMetadataProvider(string ffprobePath) : IMediaMetadataProvider
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg"
    };

    public bool Supports(string extension) => SupportedExtensions.Contains(extension);

    public async Task<MediaMetadata> ReadAsync(string filePath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-print_format");
        startInfo.ArgumentList.Add("xml");
        startInfo.ArgumentList.Add(filePath);

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Debug.WriteLine($"[VideoMetadataProvider] Start failed for '{startInfo.FileName}' ({filePath}): {ex.NativeErrorCode} {ex.Message}");
            return new MediaMetadata();
        }

        using var runningProcess = process ?? throw new InvalidOperationException("Failed to start ffprobe.");

        var output = await runningProcess.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await runningProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (runningProcess.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe exited with code {runningProcess.ExitCode} for '{filePath}'.");
        }

        return FfprobeXmlParser.Parse(output);
    }
}
