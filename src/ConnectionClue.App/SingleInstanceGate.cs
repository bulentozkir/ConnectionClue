using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace ConnectionClue.App;

/// <summary>
/// One process per interactive user session; later launches request activation through local events: plain activation,
/// or activation plus a quick check (the taskbar jump list's "Quick check" task starts a second copy with --start).
/// </summary>
internal sealed class SingleInstanceGate : IDisposable
{
    private const string Prefix = "Local\\ConnectionClue-";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate, _check;
    private RegisteredWaitHandle? _activateListener, _checkListener;

    private SingleInstanceGate(Mutex mutex, EventWaitHandle activate, EventWaitHandle check) =>
        (_mutex, _activate, _check) = (mutex, activate, check);

    /// <summary>Raised on a thread-pool thread; the argument is true when the later launch asked for a quick check.</summary>
    public event EventHandler<bool>? ActivationRequested;

    public static SingleInstanceGate? Acquire(bool quickCheck, out bool activatedExisting)
    {
        string name = Name();
        var mutex = new Mutex(initiallyOwned: true, Prefix + name, out bool createdNew);
        if (!createdNew)
        {
            // A copy from before the check event existed still accepts plain activation.
            activatedExisting = (quickCheck && SignalExisting(Prefix + name + "-Check", attempts: 10))
                || SignalExisting(Prefix + name + "-Activate", attempts: 40);
            if (activatedExisting)
            {
                mutex.Dispose();
                return null;
            }

            try
            {
                if (!mutex.WaitOne(0))
                {
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
            }
        }

        EventWaitHandle? activate = null;
        try
        {
            activatedExisting = false;
            activate = new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + name + "-Activate");
            return new SingleInstanceGate(mutex, activate, new EventWaitHandle(false, EventResetMode.AutoReset, Prefix + name + "-Check"));
        }
        catch
        {
            activate?.Dispose();
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    public void Listen()
    {
        _activateListener = ThreadPool.RegisterWaitForSingleObject(_activate,
            (_, _) => ActivationRequested?.Invoke(this, false), null, Timeout.Infinite, executeOnlyOnce: false);
        _checkListener = ThreadPool.RegisterWaitForSingleObject(_check,
            (_, _) => ActivationRequested?.Invoke(this, true), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _activateListener?.Unregister(null);
        _checkListener?.Unregister(null);
        _activate.Dispose();
        _check.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }

    private static bool SignalExisting(string eventName, int attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting(eventName);
                activate.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }

    private static string Name()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        int session = process.SessionId;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sid}|{session}")));
    }
}
