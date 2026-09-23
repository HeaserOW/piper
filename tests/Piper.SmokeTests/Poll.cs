/// <summary>
/// Waits, bounded, for a condition another thread makes true. For state the proxy publishes after
/// the bytes a test can observe, where there is nothing to await but the state itself.
/// </summary>
internal static class Poll
{
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMilliseconds = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) return false;
            await Task.Delay(10);
        }
        return true;
    }
}
