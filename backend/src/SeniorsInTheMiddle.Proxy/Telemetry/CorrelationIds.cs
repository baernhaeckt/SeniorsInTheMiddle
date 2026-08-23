namespace SeniorsInTheMiddle.Proxy.Telemetry;

/// <summary>
/// Short ids in the shape the dashboard's fixtures use. They only have to be unique for
/// the lifetime of a process, because nothing stores them.
/// </summary>
static class CorrelationIds
{
    private static long _requests;
    private static long _exchanges;

    public static string NextRequest() => $"r-{Interlocked.Increment(ref _requests):00000}";

    public static string NextExchange() => $"x-{Interlocked.Increment(ref _exchanges)}";
}
