using Garou.Domain.Models;
using Garou.Infrastructure.Sync;

namespace Garou.Tests.Sync;

public sealed class WatcherEventQueueTests
{
    [Fact]
    public async Task WatcherErrorsReportIncompleteMonitoringWithoutRequestingRescans()
    {
        var root = Path.GetTempPath();
        var queue = new WatcherEventQueue(root, 2);
        queue.ReportError(new UnauthorizedAccessException("permission denied"));
        queue.ReportError(new IOException("watch limit reached"));
        queue.Complete();

        var events = new List<FileChangeEvent>();
        await foreach (var change in queue.ReadAllAsync(TestContext.Current.CancellationToken))
            events.Add(change);

        var error = Assert.Single(events);
        Assert.Equal("permission denied", error.WatcherError);
        Assert.False(error.RequiresFullRescan);
    }

    [Fact]
    public async Task OverflowAndWatcherErrorAreBothReportedWhenQueueIsFull()
    {
        var root = Path.GetTempPath();
        var queue = new WatcherEventQueue(root, 1);
        var path = Path.Combine(root, "file.jpg");
        queue.Publish(new FileChangeEvent { LibraryRoot = root, FullPath = path, Kind = FileChangeKind.Created });
        queue.Publish(new FileChangeEvent { LibraryRoot = root, FullPath = path, Kind = FileChangeKind.Modified });
        queue.ReportError(new InternalBufferOverflowException());
        queue.ReportError(new IOException("watch limit reached"));
        queue.Complete();

        var events = new List<FileChangeEvent>();
        await foreach (var change in queue.ReadAllAsync(TestContext.Current.CancellationToken))
            events.Add(change);

        Assert.Equal(3, events.Count);
        Assert.Equal(path, events[0].FullPath);
        Assert.True(events[1].RequiresFullRescan);
        Assert.Equal("watch limit reached", events[2].WatcherError);
    }

    [Fact]
    public async Task WatcherBufferOverflowRequestsExactlyOneRescan()
    {
        var root = Path.GetTempPath();
        var queue = new WatcherEventQueue(root, 4);
        queue.ReportError(new InternalBufferOverflowException());
        queue.ReportError(new InternalBufferOverflowException());
        queue.Complete();

        var events = new List<FileChangeEvent>();
        await foreach (var change in queue.ReadAllAsync(TestContext.Current.CancellationToken))
            events.Add(change);

        Assert.True(Assert.Single(events).RequiresFullRescan);
    }
}
