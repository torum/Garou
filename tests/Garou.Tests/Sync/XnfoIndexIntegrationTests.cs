using Garou.Application.Libraries;
using Garou.Application.Queries;
using Garou.Domain.Models;
using Garou.Infrastructure.Metadata;
using Garou.Infrastructure.Persistence;
using Garou.Infrastructure.Persistence.Repositories;
using Garou.Infrastructure.Settings;
using Garou.Infrastructure.Sync;
using Microsoft.Data.Sqlite;

namespace Garou.Tests.Sync;

public sealed class XnfoIndexIntegrationTests
{
    [Fact]
    public async Task ScanAndWatcherReconcileSidecarAndSearchMetadata()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"Garou-xnfo-index-{Guid.NewGuid():N}");
        var libraryRoot = Path.Combine(root, "library");
        Directory.CreateDirectory(libraryRoot);
        var mediaPath = Path.Combine(libraryRoot, "movie.mp4");
        var otherMediaPath = Path.Combine(libraryRoot, "without-sidecar.mp4");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3], ct);
        await File.WriteAllBytesAsync(otherMediaPath, [1, 2, 3], ct);
        var factory = new SqliteConnectionFactory(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "library.db") }.ToString());
        try
        {
            await new DatabaseInitializer(factory).InitializeAsync(ct);
            var store = new XnfoStore();
            var index = new XnfoIndexService(store, factory);
            var media = new MediaFileRepository(factory);
            var indexer = new MediaIndexerService(new FileWatcherService(), new LibraryRepository(factory), media,
                new FolderRepository(factory), new LibraryExcludedFolderRepository(factory), new LibraryOperationGate(),
                new AppSettingsSession(), TimeProvider.System, index);
            var library = new LibraryDefinition { RootPath = libraryRoot };
            var metadata = new XnfoMetadata
            {
                Title = "Original title", Subtitle = "Subtitle", SortTitle = "Sort", Publisher = "Publisher",
                StudioLabel = "Studio", ReleaseDate = "2025-03", Performers = ["俳優"], Countries = ["JP"],
                Tags = ["Favorite", "quote\"value"], Genres = ["Drama"], Categories = ["Films"], ContentRating = "PG",
                Description = "Plot", Comments = "Note", CoverArt = "cover.jpg"
            };
            await store.SaveAsync(mediaPath, metadata, ct);
            var missingExceptions = 0;
            void OnFirstChanceException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
            {
                if (args.Exception is FileNotFoundException exception && exception.FileName == store.GetPath(otherMediaPath))
                    Interlocked.Increment(ref missingExceptions);
            }
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
            try
            {
                await indexer.ScanAsync(library, null, ct);
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
            }
            Assert.Equal(0, Volatile.Read(ref missingExceptions));
            Assert.NotNull(await media.GetByPathAsync(otherMediaPath, ct));
            var file = await media.GetByPathAsync(mediaPath, ct);
            Assert.NotNull(file);
            Assert.Equal("cover.jpg", file.CoverArt);
            foreach (var term in new[] { "Original title", "Subtitle", "Sort", "Publisher", "Studio", "2025-03", "俳優", "JP", "Favorite", "quote\"value", "Drama", "Films", "PG", "Plot", "Note", "cover.jpg" })
            {
                var query = new MediaQuery(null, term, MediaTypeFilter.Any, MediaSortField.Id, false, Guid.Empty, 20);
                Assert.Single(await media.SearchAsync(MediaQueryBuilder.BuildAll(query), ct));
            }

            metadata.Title = "Changed title";
            await store.SaveAsync(mediaPath, metadata, ct);
            await indexer.HandleAsync(library, new FileChangeEvent { LibraryRoot = libraryRoot, FullPath = store.GetPath(mediaPath), Kind = FileChangeKind.Modified }, ct);
            var changedQuery = new MediaQuery(null, "Changed title", MediaTypeFilter.Any, MediaSortField.Id, false, Guid.Empty, 20);
            Assert.Single(await media.SearchAsync(MediaQueryBuilder.BuildAll(changedQuery), ct));

            await File.WriteAllTextAsync(store.GetPath(mediaPath), "<xnfo>", ct);
            string? observedError = null;
            index.IndexingError += error => observedError = error;
            await indexer.HandleAsync(library, new FileChangeEvent { LibraryRoot = libraryRoot, FullPath = store.GetPath(mediaPath), Kind = FileChangeKind.Modified }, ct);
            Assert.Contains(mediaPath, observedError);
            Assert.Single(await media.SearchAsync(MediaQueryBuilder.BuildAll(changedQuery), ct));

            File.Delete(store.GetPath(mediaPath));
            await indexer.ScanAsync(library, null, ct);
            Assert.Empty(await media.SearchAsync(MediaQueryBuilder.BuildAll(changedQuery), ct));
            Assert.Null((await media.GetByPathAsync(mediaPath, ct))!.CoverArt);
            await store.SaveAsync(mediaPath, metadata, ct);
            await indexer.HandleAsync(library, new FileChangeEvent { LibraryRoot = libraryRoot, FullPath = store.GetPath(mediaPath), Kind = FileChangeKind.Created }, ct);
            Assert.Single(await media.SearchAsync(MediaQueryBuilder.BuildAll(changedQuery), ct));
            File.Delete(store.GetPath(mediaPath));
            await indexer.HandleAsync(library, new FileChangeEvent { LibraryRoot = libraryRoot, FullPath = store.GetPath(mediaPath), Kind = FileChangeKind.Deleted }, ct);
            Assert.Empty(await media.SearchAsync(MediaQueryBuilder.BuildAll(changedQuery), ct));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
