using System.Diagnostics;
using System.ComponentModel;
using System.IO;

namespace Garou.Infrastructure.Thumbnails;

public sealed class FfmpegRunner(string ffmpegPath, TimeSpan timeout)
{
    public async Task<byte[]> ReadImageAsync(string path, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-v", "error", "-i", path, "-frames:v", "1", "-f", "image2pipe", "-vcodec", "png", "pipe:1" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        using var output = new MemoryStream();
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited before it could be stopped.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(copy, error).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("Timed out decoding the image with ffmpeg.");
        }

        await Task.WhenAll(copy, error).ConfigureAwait(false);
        if (process.ExitCode != 0 || output.Length == 0)
        {
            throw new InvalidOperationException($"FFmpeg could not decode the image: {await error.ConfigureAwait(false)}");
        }

        return output.ToArray();
    }

    public async Task<bool> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        }
        catch (Win32Exception ex)
        {
            Debug.WriteLine($"[FfmpegRunner] Start failed for '{ffmpegPath}': {ex.NativeErrorCode} {ex.Message}");
            return false;
        }

        using (process)
        {
            var stdoutDrain = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderrDrain = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (InvalidOperationException)
                {
                    // The process exited between checking HasExited and calling Kill.
                }

                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }

            await Task.WhenAll(stdoutDrain, stderrDrain).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
    }
}
