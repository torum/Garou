namespace Garou.Application.Libraries;

public sealed class LibraryOperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly HashSet<string> _suppressedPaths = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    public void SuppressPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _suppressedPaths.Add(NormalizePath(path));
    }

    public bool IsPathSuppressed(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = NormalizePath(path);
        return _suppressedPaths.Any(suppressedPath => IsSameOrDescendant(suppressedPath, fullPath));
    }

    public void ClearSuppressedPathsUnder(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var fullRootPath = NormalizePath(rootPath);
        _suppressedPaths.RemoveWhere(suppressedPath => IsSameOrDescendant(fullRootPath, suppressedPath));
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrDescendant(string parentPath, string path)
    {
        if (string.Equals(parentPath, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(parentPath, path);
        return !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
