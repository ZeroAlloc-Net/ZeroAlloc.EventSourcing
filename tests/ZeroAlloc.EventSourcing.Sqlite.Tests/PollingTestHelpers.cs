using System.Diagnostics;

namespace ZeroAlloc.EventSourcing.Sqlite.Tests;

internal static class PollingTestHelpers
{
    /// <summary>Waits until the handler has received <paramref name="count"/> events, or 10 s.</summary>
    public static async Task WaitForCountAsync(List<string> received, int count)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(10))
        {
            lock (received)
            {
                if (received.Count >= count) return;
            }
            await Task.Delay(50);
        }
    }
}
