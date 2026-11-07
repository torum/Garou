namespace Garou.Application.Events;

public sealed class IndexChangeEventBus
{
    private readonly object gate = new();
    private event Action<IndexChangedNotification>? Changed;

    public IDisposable Subscribe(Action<IndexChangedNotification> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (gate)
        {
            Changed += handler;
        }

        return new Subscription(this, handler);
    }

    public void Publish(IndexChangedNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        Action<IndexChangedNotification>? handlers;
        lock (gate)
        {
            handlers = Changed;
        }

        handlers?.Invoke(notification);
    }

    private void Unsubscribe(Action<IndexChangedNotification> handler)
    {
        lock (gate)
        {
            Changed -= handler;
        }
    }

    private sealed class Subscription(IndexChangeEventBus bus, Action<IndexChangedNotification> handler) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                bus.Unsubscribe(handler);
            }
        }
    }
}

public sealed record IndexChangedNotification(string LibraryId, string FullPath, IndexChangeKind Kind);

public enum IndexChangeKind
{
    Upserted,
    Deleted,
    FullRescan
}
