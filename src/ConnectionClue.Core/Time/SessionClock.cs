namespace ConnectionClue.Core;

/// <summary>Monotonic session time in µs from session start; UTC is display-only.</summary>
public sealed class SessionClock(TimeProvider time)
{
    private readonly long _start = time.GetTimestamp();

    public DateTimeOffset StartedUtc { get; } = time.GetUtcNow();
    public TimeProvider Time => time;
    public long NowUs => ToOffsetUs(time.GetTimestamp());

    public long ToOffsetUs(long timestamp) =>
        (long)((Int128)(timestamp - _start) * 1_000_000 / time.TimestampFrequency);
}
