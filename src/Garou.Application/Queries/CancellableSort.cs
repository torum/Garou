namespace Garou.Application.Queries;

public static class CancellableSort
{
    public static IEnumerable<T> CheckItems<T>(IEnumerable<T> items, CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public static IComparer<T> Comparer<T>(IComparer<T> comparer, CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled ? new CancellationComparer<T>(comparer, cancellationToken) : comparer;

    public static T[] ToArray<T>(IEnumerable<T> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = items.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (InvalidOperationException exception) when (exception.InnerException is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class CancellationComparer<T>(IComparer<T> comparer, CancellationToken cancellationToken) : IComparer<T>
    {
        private int _comparisons;

        public int Compare(T? x, T? y)
        {
            if ((++_comparisons & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            return comparer.Compare(x!, y!);
        }
    }
}
