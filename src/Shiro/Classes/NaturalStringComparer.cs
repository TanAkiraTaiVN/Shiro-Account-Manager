#nullable enable
using System;
using System.Collections.Generic;

namespace Shiro.Classes
{
    /// <summary>
    /// Next-Gen 100% Pure Managed Alphanumeric Natural String Comparer (e.g., AD_Jackpot1, AD_Jackpot2, ..., AD_Jackpot10).
    /// Fully cross-platform, zero-allocation via ReadOnlySpan<char>, completely replacing legacy Win32 shlwapi.dll.
    /// </summary>
    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            ReadOnlySpan<char> spanX = x.AsSpan();
            ReadOnlySpan<char> spanY = y.AsSpan();

            int ix = 0;
            int iy = 0;

            while (ix < spanX.Length && iy < spanY.Length)
            {
                char cx = spanX[ix];
                char cy = spanY[iy];

                if (char.IsAsciiDigit(cx) && char.IsAsciiDigit(cy))
                {
                    int startX = ix;
                    while (ix < spanX.Length && char.IsAsciiDigit(spanX[ix]))
                        ix++;

                    int startY = iy;
                    while (iy < spanY.Length && char.IsAsciiDigit(spanY[iy]))
                        iy++;

                    ReadOnlySpan<char> numX = spanX[startX..ix];
                    ReadOnlySpan<char> numY = spanY[startY..iy];

                    ReadOnlySpan<char> trimmedX = numX.TrimStart('0');
                    ReadOnlySpan<char> trimmedY = numY.TrimStart('0');

                    if (trimmedX.Length != trimmedY.Length)
                        return trimmedX.Length.CompareTo(trimmedY.Length);

                    int numComp = trimmedX.SequenceCompareTo(trimmedY);
                    if (numComp != 0)
                        return numComp;

                    // If numeric value is identical, compare original length (leading zeros)
                    if (numX.Length != numY.Length)
                        return numX.Length.CompareTo(numY.Length);
                }
                else
                {
                    int charComp = char.ToUpperInvariant(cx).CompareTo(char.ToUpperInvariant(cy));
                    if (charComp != 0)
                        return charComp;

                    ix++;
                    iy++;
                }
            }

            return (spanX.Length - ix).CompareTo(spanY.Length - iy);
        }
    }
}
