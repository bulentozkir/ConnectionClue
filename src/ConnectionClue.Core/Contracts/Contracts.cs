using System.Net;

namespace ConnectionClue.Core;

public interface IProbe
{
    ProbeKind Kind { get; }
    Task<ProbeObservation> ExecuteAsync(ProbeRequest request, CancellationToken ct);
}

/// <summary>Bounded channel to the single writer; applies backpressure and never drops.</summary>
public interface IEvidenceWriter
{
    ValueTask EnqueueAsync(CaptureRecord record, CancellationToken ct);
}

public interface INetworkContextProvider
{
    PathContext? PredictRoute(IPAddress destination);
    PathContext? GetDefaultPath(IpFamily family);
}

/// <summary>Resolves a stream to its target from the session manifest snapshot (or live context for the gateway).</summary>
public interface ITargetResolver
{
    ResolvedTarget? Resolve(StreamKey stream);
}

public sealed record ResolvedTarget(
    string TargetId,
    string OperatorId,
    string? Host,
    int Port,
    string Path,
    IReadOnlyList<IPAddress> Addresses,
    int ExpectedStatus = 200,
    string? BodyToken = null,
    IReadOnlyList<IPNetwork>? PinnedPrefixes = null,
    IReadOnlyList<string>? ExpectedTlsIssuers = null)
{
    public IPAddress? AddressFor(RequestFamily family) =>
        Addresses.FirstOrDefault(a => family.Matches(IpFamilies.Of(a)));

    /// <summary>True when no prefixes are pinned (local/gateway targets) or the address is inside one.</summary>
    public bool IsPinned(IPAddress address)
    {
        if (PinnedPrefixes is not { Count: > 0 }) return true;
        var a = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return PinnedPrefixes.Any(p => p.Contains(a));
    }

    public bool? IsExpectedIssuer(string? issuer) =>
        ExpectedTlsIssuers is not { Count: > 0 } ? null
        : issuer is not null && ExpectedTlsIssuers.Contains(issuer, StringComparer.OrdinalIgnoreCase);
}
