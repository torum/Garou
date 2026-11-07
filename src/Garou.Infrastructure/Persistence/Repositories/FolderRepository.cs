using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence.Repositories;

public sealed class FolderRepository(SqliteConnectionFactory connectionFactory) : IFolderRepository
{
    private const string SelectColumns = "SELECT Id, Path, ParentId FROM Folder";

    public async Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        await connectionFactory.QuerySingleAsync($"{SelectColumns} WHERE Id = $value;", Map, cancellationToken, ("$value", id.ToString("N")));

    public async Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken) =>
        await connectionFactory.QuerySingleAsync($"{SelectColumns} WHERE Path = $value;", Map, cancellationToken, ("$value", path));

    public async Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken) =>
        await connectionFactory.QueryAsync($"{SelectColumns} ORDER BY Path;", Map, cancellationToken);

    public async Task<Guid> AddAsync(Folder folder, CancellationToken cancellationToken)
    {
        var id = folder.Id == Guid.Empty ? Guid.CreateVersion7() : folder.Id;
        await connectionFactory.ExecuteNonQueryAsync(
            """
            INSERT INTO Folder (Id, Path, ParentId)
            VALUES ($id, $path, $parentId);
            """,
            cancellationToken,
            ("$id", id.ToString("N")),
            ("$path", folder.Path),
            ("$parentId", folder.ParentId?.ToString("N")));
        return id;
    }

    public async Task DeleteSubtreeAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$folderId", folderId.ToString("N"));

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
        Id = Guid.Parse(reader.GetString(0)),
        Path = reader.GetString(1),
        ParentId = reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2))
    };
}
