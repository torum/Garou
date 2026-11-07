using System.Runtime.InteropServices;

namespace Garou.Application.Queries;

public sealed class LogicalFileNameComparer : IComparer<string>
{
    public static LogicalFileNameComparer Instance { get; } = new();

    private LogicalFileNameComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return StrCmpLogicalW(x, y);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return StrVersCmp(x, y);
        }

        return CompareNaturally(x, y);
    }

    [DllImport("Shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    [DllImport("libc", EntryPoint = "strverscmp", CallingConvention = CallingConvention.Cdecl)]
    private static extern int StrVersCmp(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string x,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string y);

    private static int CompareNaturally(string x, string y)
    {
        var xIndex = 0;
        var yIndex = 0;

        while (xIndex < x.Length && yIndex < y.Length)
        {
            if (char.IsAsciiDigit(x[xIndex]) && char.IsAsciiDigit(y[yIndex]))
            {
                var result = CompareDigitRuns(x, ref xIndex, y, ref yIndex);
                if (result != 0)
                {
                    return result;
                }

                continue;
            }

            var xChar = char.ToUpperInvariant(x[xIndex]);
            var yChar = char.ToUpperInvariant(y[yIndex]);
            var charResult = xChar.CompareTo(yChar);
            if (charResult != 0)
            {
                return charResult;
            }

            xIndex++;
            yIndex++;
        }

        return (xIndex == x.Length).CompareTo(yIndex == y.Length) * -1;
    }

    private static int CompareDigitRuns(string x, ref int xIndex, string y, ref int yIndex)
    {
        var xStart = xIndex;
        var yStart = yIndex;
        while (xIndex < x.Length && x[xIndex] == '0') xIndex++;
        while (yIndex < y.Length && y[yIndex] == '0') yIndex++;

        var xSignificantStart = xIndex;
        var ySignificantStart = yIndex;
        while (xIndex < x.Length && char.IsAsciiDigit(x[xIndex])) xIndex++;
        while (yIndex < y.Length && char.IsAsciiDigit(y[yIndex])) yIndex++;

        var xSignificantLength = xIndex - xSignificantStart;
        var ySignificantLength = yIndex - ySignificantStart;
        var lengthResult = xSignificantLength.CompareTo(ySignificantLength);
        if (lengthResult != 0)
        {
            return lengthResult;
        }

        var digitsResult = x.AsSpan(xSignificantStart, xSignificantLength)
            .SequenceCompareTo(y.AsSpan(ySignificantStart, ySignificantLength));
        if (digitsResult != 0)
        {
            return digitsResult;
        }

        var xLeadingZeros = xSignificantStart - xStart;
        var yLeadingZeros = ySignificantStart - yStart;
        return yLeadingZeros.CompareTo(xLeadingZeros);
    }
}
