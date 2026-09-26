using System.Runtime.InteropServices;

namespace ConnectionClue.Windows.Interop;

/// <summary>Hand-written DnsQueryEx interop: CsWin32 only emits it per CPU architecture; this layout is identical on x64 and arm64.</summary>
internal static unsafe partial class DnsInterop
{
    public const uint Version1 = 1;
    public const ulong QueryBypassCache = 0x8;
    public const ushort TypeA = 1, TypeAaaa = 28;
    public const int RequestPending = 9506, FreeRecordList = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryRequest
    {
        public uint Version;
        public char* QueryName;
        public ushort QueryType;
        public ulong QueryOptions;
        public void* DnsServerList;
        public uint InterfaceIndex;
        public delegate* unmanaged[Stdcall]<void*, QueryResult*, void> Callback;
        public void* Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryResult
    {
        public uint Version;
        public int QueryStatus;
        public ulong QueryOptions;
        public RecordHeader* Records;
        public void* Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct QueryCancel
    {
        public fixed byte Reserved[32];
    }

    /// <summary>Common DNS_RECORD header; the type-specific data union follows and is not read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RecordHeader
    {
        public RecordHeader* Next;
        public char* Name;
        public ushort Type;
        public ushort DataLength;
        public uint Flags;
        public uint Ttl;
        public uint Reserved;
    }

    [LibraryImport("dnsapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int DnsQueryEx(QueryRequest* request, QueryResult* result, QueryCancel* cancel);

    [LibraryImport("dnsapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial int DnsCancelQuery(QueryCancel* cancel);

    [LibraryImport("dnsapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial void DnsFree(void* data, int freeType);
}
