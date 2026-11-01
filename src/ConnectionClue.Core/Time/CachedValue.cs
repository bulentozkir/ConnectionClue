namespace ConnectionClue.Core;

/// <summary>
/// A value that is costly to read and rarely changes. A caller that allows it gets the held value while it is younger than
/// the lifetime; any other caller reads it fresh, and that read is held for the next one. The read must handle its own
/// failures: whatever it returns, null included, is held.
/// </summary>
public sealed class CachedValue<T>(TimeSpan lifetime, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private long? _readAt;
    private T? _held;

    public T Get(Func<T> read, bool reuse)
    {
        lock (_gate)
        {
            if (reuse && _readAt is { } at && _time.GetElapsedTime(at) < lifetime) return _held!;
            _held = read();
            _readAt = _time.GetTimestamp();
            return _held;
        }
    }
}
