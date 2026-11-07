using Garou.Application.Queries;
using Garou.Domain.Abstractions;
using Garou.Domain.Entities;
using Garou.Domain.Enums;

namespace Garou.Tests.Queries;

public sealed class MediaQueryServiceTests
{
    [Fact]
    public async Task WhenPageIsSearchedThenNextAfterIdIsLastItemId()
    {
        var search = new FakeMediaSearch([MakeMedia(1), MakeMedia(2), MakeMedia(3)]);
        var service = new MediaQueryService(search, new FakeMediaFiles(), new FakeFolders());

        var page = await service.SearchPageAsync(
            new MediaQuery(null, null, MediaTypeFilter.Any, MediaSortField.Id, false, 0, 3),
            CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(3, page.NextAfterId);
    }

    [Fact]
    public async Task WhenPageIsEmptyThenNextAfterIdIsNull()
    {
        var service = new MediaQueryService(new FakeMediaSearch([]), new FakeMediaFiles(), new FakeFolders());

        var page = await service.SearchPageAsync(
            new MediaQuery(null, null, MediaTypeFilter.Any, MediaSortField.Id, false, 0, 3),
            CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Null(page.NextAfterId);
    }

    [Fact]
    public async Task WhenPageSizeIsRequestedThenQueryIsBuiltWithClampedLimit()
    {
        var search = new FakeMediaSearch([]);
        var service = new MediaQueryService(search, new FakeMediaFiles(), new FakeFolders());

        await service.SearchPageAsync(
            new MediaQuery(null, null, MediaTypeFilter.Any, MediaSortField.Id, false, 0, 100000),
            CancellationToken.None);

        Assert.Equal(500, search.LastQuery!.Parameters["$pageSize"]);
    }

    [Fact]
    public async Task WhenMediaIdIsRequestedThenDetailsAreReturned()
    {
        var files = new FakeMediaFiles { Item = MakeMedia(7) };
        var service = new MediaQueryService(new FakeMediaSearch([]), files, new FakeFolders());

        var details = await service.GetMediaDetailsAsync(7, CancellationToken.None);

        Assert.Equal(7, details?.Id);
    }

    [Fact]
    public async Task WhenFoldersHaveParentsThenTreeNestsChildren()
    {
        var folders = new FakeFolders(
        [
            new Folder { Id = 1, Path = "C:\\Photos", ParentId = null },
            new Folder { Id = 2, Path = "C:\\Photos\\2024", ParentId = 1 },
            new Folder { Id = 3, Path = "C:\\Photos\\2025", ParentId = 1 }
        ]);
        var service = new MediaQueryService(new FakeMediaSearch([]), new FakeMediaFiles(), folders);

        var tree = await service.GetFolderTreeAsync(CancellationToken.None);

        var root = Assert.Single(tree);
        Assert.Equal(1, root.Folder.Id);
        Assert.Equal([2L, 3L], root.Children.Select(child => child.Folder.Id));
    }

    [Fact]
    public async Task WhenFolderParentIsMissingThenFolderIsRoot()
    {
        var folders = new FakeFolders([new Folder { Id = 5, Path = "D:\\Orphan", ParentId = 99 }]);
        var service = new MediaQueryService(new FakeMediaSearch([]), new FakeMediaFiles(), folders);

        var tree = await service.GetFolderTreeAsync(CancellationToken.None);

        Assert.Equal(5, Assert.Single(tree).Folder.Id);
    }

    private static MediaFile MakeMedia(long id) => new()
    {
        Id = id,
        FolderId = 1,
        FileName = $"file{id}.jpg",
        Path = $"C:\\Photos\\file{id}.jpg",
        Extension = ".jpg",
        MediaType = MediaType.Image
    };

    private sealed class FakeMediaSearch(IReadOnlyList<MediaFile> results) : IMediaSearchRepository
    {
        public BuiltQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<MediaFile>> SearchAsync(BuiltQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult(results);
        }

        public Task<long> CountAsync(BuiltQuery query, CancellationToken cancellationToken)
        {
            LastQuery = query;
            return Task.FromResult((long)results.Count);
        }
    }

    private sealed class FakeMediaFiles : IMediaFileRepository
    {
        public MediaFile? Item { get; init; }

        public Task<MediaFile?> GetByIdAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult(Item?.Id == id ? Item : null);

        public Task<MediaFile?> GetByPathAsync(string path, CancellationToken cancellationToken) => Task.FromResult<MediaFile?>(null);

        public Task<IReadOnlyList<MediaFile>> GetPageAsync(long? folderId, long afterId, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaFile>>([]);

        public Task UpsertBatchAsync(IReadOnlyList<MediaFile> mediaFiles, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateThumbnailPathAsync(long mediaFileId, string? thumbnailPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteByPathAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetPathsUnderRootAsync(string rootPath, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyDictionary<string, MediaFileSignature>> GetSignaturesUnderRootAsync(string rootPath, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, MediaFileSignature>>(new Dictionary<string, MediaFileSignature>());
    }

    private sealed class FakeFolders(IReadOnlyList<Folder>? folders = null) : IFolderRepository
    {
        public Task<Folder?> GetByIdAsync(long id, CancellationToken cancellationToken) => Task.FromResult<Folder?>(null);

        public Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken) => Task.FromResult<Folder?>(null);

        public Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(folders ?? []);

        public Task<long> AddAsync(Folder folder, CancellationToken cancellationToken) => Task.FromResult(0L);

        public Task DeleteSubtreeAsync(long folderId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
