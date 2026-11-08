using Garou.Domain.Abstractions;
using Garou.Domain.Entities;

namespace Garou.Application.Queries;

public sealed record MediaPage(IReadOnlyList<MediaFile> Items, Guid? NextAfterId);

public sealed record FolderNode(Folder Folder, IReadOnlyList<FolderNode> Children, bool IsRoot)
{
    public string DisplayName => IsRoot
        ? Folder.Path
        : Path.GetFileName(Folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public string NameText => IsRoot
        ? Path.GetFileName(Folder.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        : DisplayName;

    public string PathText => Folder.Path;

    public bool Equals(FolderNode? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

public sealed class MediaQueryService(
    IMediaSearchRepository mediaSearch,
    IMediaFileRepository mediaFiles,
    IFolderRepository folders)
{
    public async Task<MediaPage> SearchPageAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        var items = await mediaSearch
            .SearchAsync(MediaQueryBuilder.Build(query), cancellationToken)
            .ConfigureAwait(false);

        return new MediaPage(items, items.Count > 0 ? items[^1].Id : null);
    }

    public Task<IReadOnlyList<MediaFile>> SearchAllAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        return mediaSearch.SearchAsync(MediaQueryBuilder.BuildAll(query), cancellationToken);
    }

    public Task<long> CountAsync(MediaQuery query, CancellationToken cancellationToken)
    {
        return mediaSearch.CountAsync(MediaQueryBuilder.BuildCount(query), cancellationToken);
    }

    public Task<MediaFile?> GetMediaDetailsAsync(Guid id, CancellationToken cancellationToken)
    {
        return mediaFiles.GetByIdAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<FolderNode>> GetFolderTreeAsync(CancellationToken cancellationToken)
    {
        var all = await folders.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var childrenByParent = all
            .Where(folder => folder.ParentId is not null)
            .GroupBy(folder => folder.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        return all
            .Where(folder => folder.ParentId is null || !all.Any(parent => parent.Id == folder.ParentId))
            .Select(folder => BuildNode(folder, childrenByParent, isRoot: true))
            .ToList();
    }

    private static FolderNode BuildNode(Folder folder, IReadOnlyDictionary<Guid, List<Folder>> childrenByParent, bool isRoot)
    {
        var children = childrenByParent.TryGetValue(folder.Id, out var list)
            ? list.Select(child => BuildNode(child, childrenByParent, isRoot: false)).ToList()
            : [];

        return new FolderNode(folder, children, isRoot);
    }
}
