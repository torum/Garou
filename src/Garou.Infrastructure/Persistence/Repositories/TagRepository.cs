using Garou.Domain.Abstractions;
using Garou.Domain.Entities;

namespace Garou.Infrastructure.Persistence.Repositories;

public sealed class TagRepository(SqliteConnectionFactory connectionFactory) : ITagRepository
{
    public async Task<Tag?> GetByNameAsync(string name, CancellationToken cancellationToken) =>
        await connectionFactory.QuerySingleAsync(
            "SELECT Id, Name FROM Tag WHERE Name = $name;",
            reader => new Tag { Id = reader.GetInt64(0), Name = reader.GetString(1) },
            cancellationToken,
            ("$name", name));

    public async Task<long> AddAsync(Tag tag, CancellationToken cancellationToken)
    {
        var result = await connectionFactory.ExecuteScalarAsync(
            """
            INSERT INTO Tag (Name)
            VALUES ($name);
            SELECT last_insert_rowid();
            """,
            cancellationToken,
            ("$name", tag.Name));
        return (long)result!;
    }

    public async Task AssignAsync(MediaFileTag mediaFileTag, CancellationToken cancellationToken)
    {
        await connectionFactory.ExecuteNonQueryAsync(
            """
            INSERT OR IGNORE INTO MediaFileTag (MediaFileId, TagId)
            VALUES ($mediaFileId, $tagId);
            """,
            cancellationToken,
            ("$mediaFileId", mediaFileTag.MediaFileId),
            ("$tagId", mediaFileTag.TagId));
    }

    public async Task RemoveAssignmentAsync(MediaFileTag mediaFileTag, CancellationToken cancellationToken)
    {
        await connectionFactory.ExecuteNonQueryAsync(
            """
            DELETE FROM MediaFileTag
            WHERE MediaFileId = $mediaFileId AND TagId = $tagId;
            """,
            cancellationToken,
            ("$mediaFileId", mediaFileTag.MediaFileId),
            ("$tagId", mediaFileTag.TagId));
    }
}
