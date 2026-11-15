namespace Garou.Application.Queries;

public enum MediaSortField
{
    Id,
    FileName,
    ModifiedDateUtc,
    FileSize
}

public enum MediaSearchField
{
    All,
    FileName,
    Title,
    Subtitle,
    SortTitle,
    Publisher,
    StudioLabel,
    ReleaseDate,
    Performers,
    Countries,
    Tags,
    Genres,
    Categories,
    ContentRating,
    Description,
    Comments,
    CoverArt
}

[Flags]
public enum MediaTypeFilter
{
    Any = 0,
    Image = 1,
    Video = 2,
    Audio = 4,
    None = 8
}

public sealed record MediaQuery(
    Guid? FolderId,
    string? SearchText,
    MediaTypeFilter MediaType,
    MediaSortField SortField,
    bool Descending,
    Guid AfterId,
    int PageSize,
    bool IncludeDescendants = true,
    MediaSearchField SearchField = MediaSearchField.All);

public sealed record BuiltQuery(string Sql, IReadOnlyDictionary<string, object?> Parameters);

public static class MediaQueryBuilder
{
    private const int MaxPageSize = 500;

    private static readonly IReadOnlyDictionary<MediaSortField, string> SortColumns =
        new Dictionary<MediaSortField, string>
        {
            [MediaSortField.Id] = "Id",
            [MediaSortField.FileName] = "FileName",
            [MediaSortField.ModifiedDateUtc] = "ModifiedDateUtc",
            [MediaSortField.FileSize] = "FileSize"
        };

    private static readonly IReadOnlyDictionary<MediaSearchField, string> SearchExpressions =
        new Dictionary<MediaSearchField, string>
        {
            [MediaSearchField.FileName] = "FileName LIKE $search ESCAPE '\\'",
            [MediaSearchField.Title] = "x.Title LIKE $search ESCAPE '\\'",
            [MediaSearchField.Subtitle] = "x.Subtitle LIKE $search ESCAPE '\\'",
            [MediaSearchField.SortTitle] = "x.SortTitle LIKE $search ESCAPE '\\'",
            [MediaSearchField.Publisher] = "x.Publisher LIKE $search ESCAPE '\\'",
            [MediaSearchField.StudioLabel] = "x.StudioLabel LIKE $search ESCAPE '\\'",
            [MediaSearchField.ReleaseDate] = "x.ReleaseDate LIKE $search ESCAPE '\\'",
            [MediaSearchField.Performers] = "EXISTS (SELECT 1 FROM json_each(x.Performers) WHERE value LIKE $search ESCAPE '\\')",
            [MediaSearchField.Countries] = "EXISTS (SELECT 1 FROM json_each(x.Countries) WHERE value LIKE $search ESCAPE '\\')",
            [MediaSearchField.Tags] = "EXISTS (SELECT 1 FROM json_each(x.Tags) WHERE value LIKE $search ESCAPE '\\')",
            [MediaSearchField.Genres] = "EXISTS (SELECT 1 FROM json_each(x.Genres) WHERE value LIKE $search ESCAPE '\\')",
            [MediaSearchField.Categories] = "EXISTS (SELECT 1 FROM json_each(x.Categories) WHERE value LIKE $search ESCAPE '\\')",
            [MediaSearchField.ContentRating] = "x.ContentRating LIKE $search ESCAPE '\\'",
            [MediaSearchField.Description] = "x.Description LIKE $search ESCAPE '\\'",
            [MediaSearchField.Comments] = "x.Comments LIKE $search ESCAPE '\\'",
            [MediaSearchField.CoverArt] = "x.CoverArt LIKE $search ESCAPE '\\'"
        };

    private static (List<string> Conditions, Dictionary<string, object?> Parameters) BuildFilter(MediaQuery query)
    {
        var conditions = new List<string>();
        var parameters = new Dictionary<string, object?>();

        if (query.FolderId is not null)
        {
            conditions.Add(query.IncludeDescendants
                ? "FolderId IN (WITH RECURSIVE Tree(Id) AS (SELECT Id FROM Folder WHERE Id = $folderId UNION ALL SELECT f.Id FROM Folder f JOIN Tree t ON f.ParentId = t.Id) SELECT Id FROM Tree)"
                : "FolderId = $folderId");
            parameters["$folderId"] = query.FolderId?.ToString("N");
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var field = query.SearchField;
            if (field == MediaSearchField.FileName)
            {
                conditions.Add(SearchExpressions[field]);
            }
            else
            {
                var metadataExpression = field == MediaSearchField.All
                    ? string.Join(" OR ", SearchExpressions.Where(pair => pair.Key != MediaSearchField.FileName).Select(pair => pair.Value))
                    : SearchExpressions[field];
                var metadataCondition = $"EXISTS (SELECT 1 FROM XnfoMetadata x WHERE x.MediaFileId = MediaFile.Id AND ({metadataExpression}))";
                conditions.Add(field == MediaSearchField.All
                    ? $"({SearchExpressions[MediaSearchField.FileName]} OR {metadataCondition})"
                    : metadataCondition);
            }
            parameters["$search"] = $"%{EscapeLike(query.SearchText.Trim())}%";
        }

        if (query.MediaType == MediaTypeFilter.None)
        {
            conditions.Add("1 = 0");
        }
        else if (query.MediaType != MediaTypeFilter.Any)
        {
            var selectedTypes = new List<int>();
            if (query.MediaType.HasFlag(MediaTypeFilter.Image))
            {
                selectedTypes.Add((int)Domain.Enums.MediaType.Image);
            }

            if (query.MediaType.HasFlag(MediaTypeFilter.Video))
            {
                selectedTypes.Add((int)Domain.Enums.MediaType.Video);
            }

            if (query.MediaType.HasFlag(MediaTypeFilter.Audio))
            {
                selectedTypes.Add((int)Domain.Enums.MediaType.Audio);
            }

            if (selectedTypes.Count == 0)
            {
                conditions.Add("1 = 0");
            }
            else
            {
                var mediaTypeParameters = selectedTypes.Select((mediaType, index) =>
                {
                    var parameter = $"$mediaType{index}";
                    parameters[parameter] = mediaType;
                    return parameter;
                });
                conditions.Add($"MediaType IN ({string.Join(", ", mediaTypeParameters)})");
            }
        }

        return (conditions, parameters);
    }

    public static BuiltQuery BuildCount(MediaQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (conditions, parameters) = BuildFilter(query);
        var sql = $"""
            SELECT COUNT(*)
            FROM MediaFile
            WHERE {string.Join(" AND ", conditions.DefaultIfEmpty("1 = 1"))};
            """;

        return new BuiltQuery(sql, parameters);
    }

    public static BuiltQuery BuildAll(MediaQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var (conditions, parameters) = BuildFilter(query);
        var orderBy = query.SortField == MediaSortField.Id
            ? $"Id {(query.Descending ? "DESC" : "ASC")}"
            : $"{SortColumns[query.SortField]} {(query.Descending ? "DESC" : "ASC")}, Id ASC";

        var sql = $"""
            SELECT Id, Path, FileName, Extension, MediaType, FileSize, HashValue, ThumbnailPath,
                   CreatedDateUtc, ModifiedDateUtc, LastIndexedUtc, FolderId,
                   (SELECT CoverArt FROM XnfoMetadata WHERE MediaFileId = MediaFile.Id) AS CoverArt
            FROM MediaFile
            WHERE {string.Join(" AND ", conditions.DefaultIfEmpty("1 = 1"))}
            ORDER BY {orderBy};
            """;

        return new BuiltQuery(sql, parameters);
    }

    public static BuiltQuery Build(MediaQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var pageSize = Math.Clamp(query.PageSize, 1, MaxPageSize);
        var sortColumn = SortColumns[query.SortField];
        var direction = query.Descending ? "DESC" : "ASC";
        var (conditions, parameters) = BuildFilter(query);
        conditions.Insert(0, "Id > $afterId");
        parameters["$afterId"] = query.AfterId.ToString("N");
        parameters["$pageSize"] = pageSize;

        var orderBy = query.SortField == MediaSortField.Id
            ? $"Id {direction}"
            : $"{sortColumn} {direction}, Id ASC";

        var sql = $"""
            SELECT Id, Path, FileName, Extension, MediaType, FileSize, HashValue, ThumbnailPath,
                   CreatedDateUtc, ModifiedDateUtc, LastIndexedUtc, FolderId,
                   (SELECT CoverArt FROM XnfoMetadata WHERE MediaFileId = MediaFile.Id) AS CoverArt
            FROM MediaFile
            WHERE {string.Join(" AND ", conditions)}
            ORDER BY {orderBy}
            LIMIT $pageSize;
            """;

        return new BuiltQuery(sql, parameters);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
