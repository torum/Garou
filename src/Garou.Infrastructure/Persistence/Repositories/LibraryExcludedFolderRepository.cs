using Garou.Domain.Abstractions;
using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence.Repositories;

public sealed class LibraryExcludedFolderRepository(SqliteConnectionFactory connectionFactory) : ILibraryExcludedFolderRepository
{
    public async Task<IReadOnlyList<string>> GetPathsAsync(Guid libraryId, CancellationToken cancellationToken) =>
        await connectionFactory.QueryAsync(
            "SELECT Path FROM LibraryExcludedFolder WHERE LibraryId = $libraryId ORDER BY Path;",
            reader => reader.GetString(0),
            cancellationToken,
            ("$libraryId", libraryId.ToString("N")));

    public async Task AddAsync(Guid libraryId, string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        await connectionFactory.ExecuteNonQueryAsync(
            "INSERT OR IGNORE INTO LibraryExcludedFolder (LibraryId, Path) VALUES ($libraryId, $path);",
            cancellationToken,
            ("$libraryId", libraryId.ToString("N")),
            ("$path", fullPath));
    }
}
