using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence.Repositories;

public sealed class FolderRepository(SqliteConnectionFactory connectionFactory) : IFolderRepository
{
    private const string SelectColumns = "SELECT Id, Path, ParentId FROM Folder";

    public async Task<Folder?> GetByIdAsync(long id, CancellationToken cancellationToken) =>
        await connectionFactory.QuerySingleAsync($"{SelectColumns} WHERE Id = $value;", Map, cancellationToken, ("$value", id));

    public async Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken) =>
        await connectionFactory.QuerySingleAsync($"{SelectColumns} WHERE Path = $value;", Map, cancellationToken, ("$value", path));

    public async Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken) =>
        await connectionFactory.QueryAsync($"{SelectColumns} ORDER BY Path;", Map, cancellationToken);

    public async Task<long> AddAsync(Folder folder, CancellationToken cancellationToken)
    {
        var result = await connectionFactory.ExecuteScalarAsync(
            """
            INSERT INTO Folder (Path, ParentId)
            VALUES ($path, $parentId);
            SELECT last_insert_rowid();
            """,
            cancellationToken,
            ("$path", folder.Path),
            ("$parentId", folder.ParentId));
        return (long)result!;
    }

    public async Task DeleteSubtreeAsync(long folderId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(folderId);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$folderId", folderId);

        command.CommandText =
            """
            WITH RECURSIVE FolderTree(Id) AS (
                SELECT Id FROM Folder WHERE Id = $folderId
                UNION ALL
                SELECT folder.Id FROM Folder folder JOIN FolderTree tree ON folder.ParentId = tree.Id
            )
            DELETE FROM MediaFileTag
            WHERE MediaFileId IN (
                SELECT Id FROM MediaFile WHERE FolderId IN (SELECT Id FROM FolderTree)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.CommandText =
            """
            WITH RECURSIVE FolderTree(Id) AS (
                SELECT Id FROM Folder WHERE Id = $folderId
                UNION ALL
                SELECT folder.Id FROM Folder folder JOIN FolderTree tree ON folder.ParentId = tree.Id
            )
            DELETE FROM MediaFile
            WHERE FolderId IN (SELECT Id FROM FolderTree);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        command.CommandText =
            """
            WITH RECURSIVE FolderTree(Id) AS (
                SELECT Id FROM Folder WHERE Id = $folderId
                UNION ALL
                SELECT folder.Id FROM Folder folder JOIN FolderTree tree ON folder.ParentId = tree.Id
            )
            DELETE FROM Folder
            WHERE Id IN (SELECT Id FROM FolderTree);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    private static Folder Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Path = reader.GetString(1),
        ParentId = reader.ReadNullable<long>(2)
    };
}
