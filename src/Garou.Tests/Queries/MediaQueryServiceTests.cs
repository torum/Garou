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
        var ids = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToArray();
        var search = new FakeMediaSearch(ids.Select(MakeMedia).ToArray());
        var service = new MediaQueryService(search, new FakeMediaFiles(), new FakeFolders());

        var page = await service.SearchPageAsync(
            new MediaQuery(null, null, MediaTypeFilter.Any, MediaSortField.Id, false, Guid.Empty, 3),
            CancellationToken.None);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(ids[^1], page.NextAfterId);
    }

    [Fact]
    public async Task WhenPageIsEmptyThenNextAfterIdIsNull()
    {
        var service = new MediaQueryService(new FakeMediaSearch([]), new FakeMediaFiles(), new FakeFolders());

        var page = await service.SearchPageAsync(
            new MediaQuery(null, null, MediaTypeFilter.Any, MediaSortField.Id, false, Guid.Empty, 3),
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
            new MediaQuery(null, null, MediaTypeFilter.Any, MediaSortField.Id, false, Guid.Empty, 100000),
            CancellationToken.None);

        Assert.Equal(500, search.LastQuery!.Parameters["$pageSize"]);
    }

    [Fact]
    public async Task WhenMediaIdIsRequestedThenDetailsAreReturned()
    {
        var id = Guid.CreateVersion7();
        var files = new FakeMediaFiles { Item = MakeMedia(id) };
        var service = new MediaQueryService(new FakeMediaSearch([]), files, new FakeFolders());

        var details = await service.GetMediaDetailsAsync(id, CancellationToken.None);

        Assert.Equal(id, details?.Id);
    }

    [Fact]
    public async Task WhenFoldersHaveParentsThenTreeNestsChildren()
    {
        var folders = new FakeFolders(
        [
            new Folder { Path = "C:\\Photos" },
            new Folder { Path = "C:\\Photos\\2024" },
            new Folder { Path = "C:\\Photos\\2025" }
        ]);
        var rootFolder = folders.FolderItems[0];
        folders.SetFolders([
            rootFolder,
            new Folder { Path = "C:\\Photos\\2024", ParentId = rootFolder.Id },
            new Folder { Path = "C:\\Photos\\2025", ParentId = rootFolder.Id }
        ]);
        var service = new MediaQueryService(new FakeMediaSearch([]), new FakeMediaFiles(), folders);

        var tree = await service.GetFolderTreeAsync(CancellationToken.None);

        var root = Assert.Single(tree);
        Assert.Equal(rootFolder.Id, root.Folder.Id);
        Assert.Equal(folders.FolderItems.Skip(1).Select(folder => folder.Id), root.Children.Select(child => child.Folder.Id));
    }

    [Fact]
    public async Task WhenFolderParentIsMissingThenFolderIsRoot()
    {
        var folders = new FakeFolders([new Folder { Path = "D:\\Orphan", ParentId = Guid.CreateVersion7() }]);
        var service = new MediaQueryService(new FakeMediaSearch([]), new FakeMediaFiles(), folders);

        var tree = await service.GetFolderTreeAsync(CancellationToken.None);

        Assert.Equal(folders.FolderItems[0].Id, Assert.Single(tree).Folder.Id);
    }

    private static MediaFile MakeMedia(Guid id) => new()
    {
        Id = id,
        FolderId = Guid.CreateVersion7(),
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

        public Task<MediaFile?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Item?.Id == id ? Item : null);

        public Task<MediaFile?> GetByPathAsync(string path, CancellationToken cancellationToken) => Task.FromResult<MediaFile?>(null);

        public Task<IReadOnlyList<MediaFile>> GetPageAsync(Guid? folderId, Guid afterId, int pageSize, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaFile>>([]);

        public Task UpsertBatchAsync(IReadOnlyList<MediaFile> mediaFiles, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateThumbnailPathAsync(Guid mediaFileId, string? thumbnailPath, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteByPathAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetPathsUnderRootAsync(string rootPath, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyDictionary<string, MediaFileSignature>> GetSignaturesUnderRootAsync(string rootPath, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, MediaFileSignature>>(new Dictionary<string, MediaFileSignature>());
    }

    private sealed class FakeFolders(IReadOnlyList<Folder>? folders = null) : IFolderRepository
    {
        public IReadOnlyList<Folder> FolderItems { get; private set; } = folders ?? [];

        public void SetFolders(IReadOnlyList<Folder> items) => FolderItems = items;

        public Task<Folder?> GetByIdAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<Folder?>(null);

        public Task<Folder?> GetByPathAsync(string path, CancellationToken cancellationToken) => Task.FromResult<Folder?>(null);

        public Task<IReadOnlyList<Folder>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult(FolderItems);

        public Task<Guid> AddAsync(Folder folder, CancellationToken cancellationToken) => Task.FromResult(Guid.CreateVersion7());

        public Task DeleteSubtreeAsync(Guid folderId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
