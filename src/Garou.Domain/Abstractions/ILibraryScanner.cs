using Garou.Domain.Models;

namespace Garou.Domain.Abstractions;

public interface ILibraryScanner
{
    Task ScanAsync(LibraryDefinition library, IProgress<ScanProgress>? progress, CancellationToken cancellationToken);
}
