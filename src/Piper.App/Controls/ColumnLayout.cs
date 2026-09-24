namespace Piper.App.Controls;

/// <summary>Shares a grid's width between its columns. Free of WinForms, so it can be tested.</summary>
internal static class ColumnLayout
{
    /// <summary>
    /// Gives every column its minimum, then shares whatever width is left by weight; the last
    /// weighted column takes the rounding remainder, so the result fills <paramref name="available"/>
    /// exactly. Below the summed minimums the minimums come back unchanged: the grid scrolls
    /// sideways rather than crushing a column below what its text needs.
    /// </summary>
    public static int[] Fit(int available, ReadOnlySpan<int> minimums, ReadOnlySpan<int> weights)
    {
        if (minimums.Length != weights.Length)
            throw new ArgumentException("Every column needs both a minimum and a weight.", nameof(weights));

        var minimumTotal = 0;
        var remainingWeight = 0;
        for (var index = 0; index < minimums.Length; index++)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(minimums[index], nameof(minimums));
            ArgumentOutOfRangeException.ThrowIfNegative(weights[index], nameof(weights));
            minimumTotal += minimums[index];
            remainingWeight += weights[index];
        }

        var remainingExtra = Math.Max(0, available - minimumTotal);
        var widths = new int[minimums.Length];
        for (var index = 0; index < minimums.Length; index++)
        {
            var weight = weights[index];
            var extra = weight == 0 ? 0 : (int)((long)remainingExtra * weight / remainingWeight);
            widths[index] = minimums[index] + extra;
            remainingExtra -= extra;
            remainingWeight -= weight;
        }

        return widths;
    }
}
