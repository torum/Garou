using Garou.Domain.Models;

namespace Garou.Domain.Abstractions;

public interface IFileWatcherService
{
    IAsyncEnumerable<FileChangeEvent> WatchAsync(LibraryDefinition library, CancellationToken cancellationToken);
}
