using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private bool _disposed;

    private SingleInstanceGate(Mutex mutex, EventWaitHandle activate, EventWaitHandle check) =>
        (_mutex, _activate, _check) = (mutex, activate, check);

    /// <summary>Raised on a thread-pool thread; the argument is true when the later launch asked for a quick check.</summary>
    public event EventHandler<bool>? ActivationRequested;

    public static SingleInstanceGate? Acquire(bool quickCheck, out bool activatedExisting) =>
        Acquire(Name(), quickCheck, out activatedExisting);

    /// <param name="name">Which instance is meant: one per user and session in the app, a private one in tests.</param>
    internal static SingleInstanceGate? Acquire(string name, bool quickCheck, out bool activatedExisting)
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, Prefix + name, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // The running copy is this user's but runs with other rights (as administrator): this one cannot even signal it.
            activatedExisting = false;
            return null;
        }

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
        if (_disposed) return;
        _disposed = true;
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
                AllowForeground();
                activate.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
        return false;
    }

    // The user just started this launch, so Windows lets it bring a window forward; the running copy, started long before,
    // may not and would only flash its taskbar button. Passing the right on is what makes the second launch switch to it.
    private static void AllowForeground()
    {
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) is not { Length: > 0 } name) return;
        try
        {
            foreach (var other in Process.GetProcessesByName(name))
            {
                using (other)
                {
                    if (other.Id != Environment.ProcessId) AllowSetForegroundWindow(other.Id);
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Best effort, and never a reason to skip the hand-off itself: without the right the running copy still opens its
            // window, it just may stay behind the others.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    private static string Name()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        int session = process.SessionId;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{sid}|{session}")));
    }
}
