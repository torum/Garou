using System.Diagnostics;
using System.ComponentModel;
using System.IO;

namespace Garou.Infrastructure.Thumbnails;

public sealed class FfmpegRunner(string ffmpegPath, TimeSpan timeout)
{
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
