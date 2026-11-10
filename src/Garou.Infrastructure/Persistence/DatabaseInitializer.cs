using Microsoft.Data.Sqlite;

namespace Garou.Infrastructure.Persistence;

public sealed class DatabaseInitializer(SqliteConnectionFactory connectionFactory)
{
    private const string SchemaSql =
        """
        CREATE TABLE IF NOT EXISTS Folder
        (
            Id TEXT NOT NULL PRIMARY KEY,
            Path TEXT NOT NULL UNIQUE,
            ParentId TEXT NULL REFERENCES Folder(Id)
        );

        CREATE TABLE IF NOT EXISTS MediaFile
        (
            Id TEXT NOT NULL PRIMARY KEY,
            Path TEXT NOT NULL UNIQUE,
            FileName TEXT NOT NULL,
            Extension TEXT NOT NULL,
            MediaType INTEGER NOT NULL,
            FileSize INTEGER,
            HashValue TEXT,
            ThumbnailPath TEXT,
            CreatedDateUtc TEXT,
            ModifiedDateUtc TEXT,
            LastIndexedUtc TEXT,
            FolderId TEXT NULL REFERENCES Folder(Id)
        );

        CREATE INDEX IF NOT EXISTS IX_MediaFile_FolderId_Id ON MediaFile(FolderId, Id);
        CREATE INDEX IF NOT EXISTS IX_MediaFile_MediaType ON MediaFile(MediaType);
        CREATE INDEX IF NOT EXISTS IX_MediaFile_Extension ON MediaFile(Extension);
        CREATE INDEX IF NOT EXISTS IX_MediaFile_ModifiedDateUtc ON MediaFile(ModifiedDateUtc);
        CREATE INDEX IF NOT EXISTS IX_MediaFile_FileSize ON MediaFile(FileSize);

        CREATE TABLE IF NOT EXISTS Tag
        (
            Id TEXT NOT NULL PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS MediaFileTag
        (
            MediaFileId TEXT NOT NULL REFERENCES MediaFile(Id),
            TagId TEXT NOT NULL REFERENCES Tag(Id),
            PRIMARY KEY(MediaFileId, TagId)
        );

        CREATE INDEX IF NOT EXISTS IX_MediaFileTag_TagId ON MediaFileTag(TagId);

        CREATE TABLE IF NOT EXISTS ThumbnailCacheEntry
        (
            ThumbnailPath TEXT NOT NULL PRIMARY KEY,
            SizeBytes INTEGER NOT NULL,
            LastAccessUtc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS IX_ThumbnailCacheEntry_LastAccessUtc ON ThumbnailCacheEntry(LastAccessUtc);

        CREATE TABLE IF NOT EXISTS Library
        (
            Id TEXT NOT NULL PRIMARY KEY,
            RootPath TEXT NOT NULL UNIQUE,
            IsEnabled INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS LibraryExcludedFolder
        (
            LibraryId TEXT NOT NULL REFERENCES Library(Id) ON DELETE CASCADE,
            Path TEXT NOT NULL COLLATE NOCASE,
            PRIMARY KEY (LibraryId, Path)
        );
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
