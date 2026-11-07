using Garou.Domain.Abstractions;
using Garou.Domain.Entities;

namespace Garou.Infrastructure.Persistence.Repositories;

public sealed class TagRepository(SqliteConnectionFactory connectionFactory) : ITagRepository
{
    public async Task<Tag?> GetByNameAsync(string name, CancellationToken cancellationToken) =>
        await connectionFactory.QuerySingleAsync(
            "SELECT Id, Name FROM Tag WHERE Name = $name;",
            reader => new Tag { Id = Guid.Parse(reader.GetString(0)), Name = reader.GetString(1) },
            cancellationToken,
            ("$name", name));

    public async Task<Guid> AddAsync(Tag tag, CancellationToken cancellationToken)
    {
        var id = tag.Id == Guid.Empty ? Guid.CreateVersion7() : tag.Id;
        await connectionFactory.ExecuteNonQueryAsync(
            """
            INSERT INTO Tag (Id, Name)
            VALUES ($id, $name);
            """,
            cancellationToken,
            ("$id", id.ToString("N")),
            ("$name", tag.Name));
        return id;
    }

    public async Task AssignAsync(MediaFileTag mediaFileTag, CancellationToken cancellationToken)
    {
        await connectionFactory.ExecuteNonQueryAsync(
            """
            INSERT OR IGNORE INTO MediaFileTag (MediaFileId, TagId)
            VALUES ($mediaFileId, $tagId);
            """,
            cancellationToken,
            ("$mediaFileId", mediaFileTag.MediaFileId.ToString("N")),
            ("$tagId", mediaFileTag.TagId.ToString("N")));
    }

    public async Task RemoveAssignmentAsync(MediaFileTag mediaFileTag, CancellationToken cancellationToken)
    {
        await connectionFactory.ExecuteNonQueryAsync(
            """
            DELETE FROM MediaFileTag
            WHERE MediaFileId = $mediaFileId AND TagId = $tagId;
            """,
            cancellationToken,
            ("$mediaFileId", mediaFileTag.MediaFileId.ToString("N")),
            ("$tagId", mediaFileTag.TagId.ToString("N")));
    }
}
