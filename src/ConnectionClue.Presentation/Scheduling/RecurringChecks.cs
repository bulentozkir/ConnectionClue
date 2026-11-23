namespace ConnectionClue.Presentation.Scheduling;

/// <summary>
/// Runs a check every interval (start to start) while enabled. In-process only: no Windows service, scheduled task
/// or login item. A tick that arrives during a long check runs at most once afterwards; the check itself skips
/// when another one is already running.
/// </summary>
public sealed class RecurringChecks(TimeProvider time, Func<Task> runCheck, Action<Exception>? onError = null) : IDisposable
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(60);
    private CancellationTokenSource? _cts;

    public bool IsEnabled => _cts is not null;

    public void Configure(bool enabled, TimeSpan interval, bool runNow = false)
    {
        if (!enabled)
        {
            Stop();
            return;
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(interval, MinimumInterval);
        var timer = new PeriodicTimer(interval, time);
        Stop();
        _cts = new CancellationTokenSource();
        _ = LoopAsync(timer, runNow, _cts.Token);
    }

    public void Dispose() => Stop();

    private void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(PeriodicTimer timer, bool runNow, CancellationToken ct)
    {
        using (timer)
        {
            try
            {
                if (runNow) await RunAsync();
                while (await timer.WaitForNextTickAsync(ct)) await RunAsync();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }
    }

    // One failed check must not end the schedule.
    private async Task RunAsync()
    {
        try { await runCheck(); }
        catch (OperationCanceledException) { } // Cancelling one check does not disable future ticks.
        catch (Exception e) { onError?.Invoke(e); }
    }
}
