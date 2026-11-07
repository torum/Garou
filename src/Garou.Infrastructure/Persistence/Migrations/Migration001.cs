namespace Garou.Infrastructure.Persistence.Migrations;

internal static class Migration001
{
    public const string Sql =
        """
        CREATE TABLE Folder
        (
            Id INTEGER PRIMARY KEY,
            Path TEXT NOT NULL UNIQUE,
            ParentId INTEGER NULL
        );

        CREATE TABLE MediaFile
        (
            Id INTEGER PRIMARY KEY,
            Path TEXT NOT NULL UNIQUE,
            FileName TEXT NOT NULL,
            Extension TEXT NOT NULL,
            MediaType INTEGER NOT NULL,
            FileSize INTEGER,
            MimeType TEXT,
            Width INTEGER,
            Height INTEGER,
            DurationSeconds REAL,
            HashValue TEXT,
            ThumbnailPath TEXT,
            CreatedDateUtc TEXT,
            ModifiedDateUtc TEXT,
            LastIndexedUtc TEXT,
            FolderId INTEGER
        );

        CREATE INDEX IX_MediaFile_FolderId_Id ON MediaFile(FolderId, Id);
        CREATE INDEX IX_MediaFile_MediaType ON MediaFile(MediaType);
        CREATE INDEX IX_MediaFile_Extension ON MediaFile(Extension);
        CREATE INDEX IX_MediaFile_ModifiedDateUtc ON MediaFile(ModifiedDateUtc);
        CREATE INDEX IX_MediaFile_FileSize ON MediaFile(FileSize);

        CREATE TABLE Tag
        (
            Id INTEGER PRIMARY KEY,
            Name TEXT NOT NULL UNIQUE
        );

        CREATE TABLE MediaFileTag
        (
            MediaFileId INTEGER NOT NULL,
            TagId INTEGER NOT NULL,
            PRIMARY KEY(MediaFileId, TagId)
        );

        CREATE INDEX IX_MediaFileTag_TagId ON MediaFileTag(TagId);

        CREATE TABLE ThumbnailCacheEntry
        (
            ThumbnailPath TEXT NOT NULL PRIMARY KEY,
            SizeBytes INTEGER NOT NULL,
            LastAccessUtc TEXT NOT NULL
        );

        CREATE INDEX IX_ThumbnailCacheEntry_LastAccessUtc ON ThumbnailCacheEntry(LastAccessUtc);
        """;
}
