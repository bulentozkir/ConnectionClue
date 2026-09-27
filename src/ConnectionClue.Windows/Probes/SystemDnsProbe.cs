using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ConnectionClue.Core;
using static ConnectionClue.Windows.Interop.DnsInterop;

namespace ConnectionClue.Windows.Probes;

/// <summary>
/// System-resolver lookup via DnsQueryEx with DNS_QUERY_BYPASS_CACHE: keeps servers, NRPT, VPN policy and DoH,
/// never reads or flushes the OS cache. A and AAAA are separate streams.
/// </summary>
public sealed class SystemDnsProbe(SessionClock clock, ITargetResolver targets) : IProbe
{
    public ProbeKind Kind => ProbeKind.SystemDns;

    public async Task<ProbeObservation> ExecuteAsync(ProbeRequest request, CancellationToken ct)
    {
        if (request.Stream.Family == RequestFamily.Any) return Observations.Skipped(request, "FamilyRequired");
        if (targets.Resolve(request.Stream)?.Host is not { Length: > 0 } host) return Observations.Skipped(request, "NoName");

        bool v6 = request.Stream.Family == RequestFamily.IPv6;
        long started = clock.NowUs;
        if (ct.IsCancellationRequested)
            return Observations.NoDuration(request, ProbeStatus.Cancelled, Attribution.Unknown, null, started, started, null);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        var operation = DnsOperation.Start(host, v6 ? TypeAaaa : TypeA);
        DnsResult result;
        using (timeout.Token.Register(static state => ((DnsOperation)state!).Cancel(), operation))
            result = await operation.Completion.ConfigureAwait(false);
        long ended = clock.NowUs;

        var (status, code) = ProbeClassification.FromDns(result.Status, result.Matching);
        if (status == ProbeStatus.Cancelled && !ct.IsCancellationRequested) (status, code) = (ProbeStatus.Timeout, null);
        var detail = new DnsDetail(result.Status, result.Matching);
        IpFamily? family = result.Matching > 0 ? (v6 ? IpFamily.IPv6 : IpFamily.IPv4) : null;
        return status is ProbeStatus.Timeout or ProbeStatus.Cancelled
            ? Observations.NoDuration(request, status, Attribution.Unknown, null, started, ended, code, detail)
            : new ProbeObservation(request, status, Attribution.Unknown, family, started, ended, ended - started,
                TimingSource.UserMode, code, detail);
    }
}

internal readonly record struct DnsResult(int Status, int Matching);

/// <summary>
/// One asynchronous DnsQueryEx call. Native memory lives until the completion has run and no cancel is in flight
/// (reference count), so a timeout racing completion can never touch freed memory.
/// </summary>
internal sealed unsafe class DnsOperation
{
    private readonly TaskCompletionSource<DnsResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ushort _type;
    private byte* _block;
    private GCHandle _self;
    private int _refs = 1;
    private int _completed;

    private DnsOperation(ushort type) => _type = type;

    public Task<DnsResult> Completion => _completion.Task;

    private QueryRequest* Request => (QueryRequest*)_block;
    private QueryResult* Result => (QueryResult*)(_block + sizeof(QueryRequest));
    private QueryCancel* CancelHandle => (QueryCancel*)(_block + sizeof(QueryRequest) + sizeof(QueryResult));
    private char* Name => (char*)(_block + sizeof(QueryRequest) + sizeof(QueryResult) + sizeof(QueryCancel));

    public static DnsOperation Start(string name, ushort type)
    {
        var op = new DnsOperation(type);
        op.Begin(name);
        return op;
    }

    private void Begin(string name)
    {
        int size = sizeof(QueryRequest) + sizeof(QueryResult) + sizeof(QueryCancel) + (name.Length + 1) * sizeof(char);
        _block = (byte*)NativeMemory.AllocZeroed((nuint)size);
        name.AsSpan().CopyTo(new Span<char>(Name, name.Length));
        _self = GCHandle.Alloc(this);
        *Request = new QueryRequest
        {
            Version = Version1,
            QueryName = Name,
            QueryType = _type,
            QueryOptions = QueryBypassCache,
            Callback = &OnComplete,
            Context = (void*)GCHandle.ToIntPtr(_self),
        };
        Result->Version = Version1;
        int rc = DnsQueryEx(Request, Result, CancelHandle);
        if (rc != RequestPending) Complete(Result, rc); // synchronous completion: the callback will not run
    }

    public void Cancel()
    {
        int refs;
        do
        {
            refs = Volatile.Read(ref _refs);
            if (refs == 0) return;
        } while (Interlocked.CompareExchange(ref _refs, refs + 1, refs) != refs);

        if (Volatile.Read(ref _completed) == 0) _ = DnsCancelQuery(CancelHandle); // completion reports ERROR_CANCELLED
        Release();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnComplete(void* context, QueryResult* result)
    {
        if (GCHandle.FromIntPtr((nint)context).Target is DnsOperation op) op.Complete(result, result->QueryStatus);
    }

    private void Complete(QueryResult* result, int status)
    {
        int matching = 0;
        for (var r = result->Records; r is not null; r = r->Next)
            if (r->Type == _type) matching++;
        if (result->Records is not null) DnsFree(result->Records, FreeRecordList);
        Volatile.Write(ref _completed, 1);
        _completion.TrySetResult(new DnsResult(status, matching));
        Release();
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _refs) != 0) return;
        NativeMemory.Free(_block);
        _block = null;
        _self.Free();
    }
}
