using Garou.Domain.Abstractions;
using Garou.Domain.Models;

namespace Garou.Application.Libraries;

public sealed class LibraryCommands(ILibraryRepository libraries, ILibraryScanner scanner)
{
    public async Task<Guid> AddLibraryAsync(string rootPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var fullPath = Path.GetFullPath(rootPath);
        var existing = await libraries.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Any(library => string.Equals(library.RootPath, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A library with root '{fullPath}' already exists.");
        }

        var id = Guid.CreateVersion7();
        await libraries.AddAsync(new LibraryDefinition { Id = id, RootPath = fullPath }, cancellationToken).ConfigureAwait(false);
        return id;
    }

    public Task RemoveLibraryAsync(Guid id, CancellationToken cancellationToken)
    {
        return libraries.RemoveAsync(id, cancellationToken);
    }

    public Task EnableLibraryAsync(Guid id, CancellationToken cancellationToken)
    {
        return libraries.SetEnabledAsync(id, true, cancellationToken);
    }

    public Task DisableLibraryAsync(Guid id, CancellationToken cancellationToken)
    {
        return libraries.SetEnabledAsync(id, false, cancellationToken);
    }

    public async Task RefreshLibraryAsync(Guid id, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var library = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        await scanner.ScanAsync(library, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task RescanLibraryByRootAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var fullPath = Path.GetFullPath(rootPath);
        var all = await libraries.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var library = all.FirstOrDefault(item => string.Equals(item.RootPath, fullPath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No library exists for root '{fullPath}'.");
        await scanner.ScanAsync(library, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReindexLibraryAsync(Guid id, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var library = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        library.IsOnline = true;
        await scanner.ScanAsync(library, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<LibraryDefinition> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var all = await libraries.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return all.FirstOrDefault(library => library.Id == id)
            ?? throw new InvalidOperationException($"Library '{id}' does not exist.");
    }
}
