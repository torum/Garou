using System.Threading.Channels;
using Garou.Domain.Models;

namespace Garou.Infrastructure.Sync;

internal sealed class WatcherEventQueue(string rootPath, int capacity)
{
    private readonly Channel<FileChangeEvent> _channel = Channel.CreateBounded<FileChangeEvent>(new BoundedChannelOptions(capacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private int _overflowed;
    private int _errorReported;
    private string? _pendingError;

    public void Publish(FileChangeEvent change)
    {
        if (!_channel.Writer.TryWrite(change) && Interlocked.Exchange(ref _overflowed, 1) == 0)
        {
            _channel.Writer.TryWrite(Signal());
        }
    }

    public void ReportError(Exception exception)
    {
        if (exception is InternalBufferOverflowException)
        {
            if (Interlocked.Exchange(ref _overflowed, 1) == 0)
            {
                _channel.Writer.TryWrite(Signal());
            }
            return;
        }

        System.Diagnostics.Debug.WriteLine($"File monitoring error for '{rootPath}': {exception}");
        if (Interlocked.Exchange(ref _errorReported, 1) == 0)
        {
            Interlocked.Exchange(ref _pendingError, exception.Message);
            _channel.Writer.TryWrite(Signal());
        }
    }

    public async IAsyncEnumerable<FileChangeEvent> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var change in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!change.RequiresFullRescan)
            {
                yield return change;
            }

            if (Interlocked.Exchange(ref _overflowed, 0) == 1)
            {
                yield return Signal();
            }

            var error = Interlocked.Exchange(ref _pendingError, null);
            if (error is not null)
            {
                yield return new FileChangeEvent
                {
                    LibraryRoot = rootPath,
                    FullPath = rootPath,
                    WatcherError = error
                };
            }
        }
    }

    public void Complete() => _channel.Writer.TryComplete();

    private FileChangeEvent Signal() => new()
    {
        LibraryRoot = rootPath,
        FullPath = rootPath,
        Kind = FileChangeKind.Modified,
        RequiresFullRescan = true
    };
}
