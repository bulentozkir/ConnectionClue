using System.Runtime.InteropServices;

namespace ConnectionClue.App;

/// <summary>Gives memory back while the app only waits in the notification area.</summary>
internal static class MemoryTrim
{
    /// <param name="afterWindow">A window was just released: its native surfaces and caches are freed by finalizers, which only
    /// run after the first collection, so collect once more after they have.</param>
    public static void Now(bool afterWindow = false)
    {
        // Compact the whole heap and hand the freed memory back to Windows (not just reuse it later), the large object heap
        // included: a window's objects and surfaces are a large part of what the process holds after it closes.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        if (afterWindow)
        {
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        // Drop the pages the process is not using right now from its working set; Windows returns whatever a check touches.
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(nint process, nint minimum, nint maximum);
}
