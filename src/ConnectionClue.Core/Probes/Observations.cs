using static ConnectionClue.Core.ProbeStatus;

namespace ConnectionClue.Core;

public static class Observations
{
    public static ProbeObservation Skipped(ProbeRequest r, string reason, Attribution attribution = Attribution.Unknown) =>
        new(r, ProbeStatus.Skipped, attribution, null, null, null, null, null, reason, new EmptyDetail());

    /// <summary>An outcome without a measured duration (timeouts, cancellations, denials, internal errors).</summary>
    public static ProbeObservation NoDuration(
        ProbeRequest r, ProbeStatus status, Attribution attribution, IpFamily? family, long startedUs, long endedUs,
        string? errorCode, ProbeDetail? detail = null) =>
        new(r, status, attribution, family, startedUs, endedUs, null, null, errorCode, detail ?? new EmptyDetail());
}

/// <summary>Mirrors the ProbeObservation CHECK constraints in schema.sql so violations surface before persistence.</summary>
public static class ObservationInvariants
{
    private static readonly HashSet<ProbeStatus> NoDurationStatuses =
        [ProbeStatus.Timeout, Cancelled, ProbeStatus.Skipped, PermissionDenied, Unsupported, InternalError];

    private static readonly HashSet<ProbeStatus> HttpsOnly = [TlsFailure, HttpUnexpected, ProxyAuthRequired, RateLimited];

    public static IReadOnlyList<string> Violations(ProbeObservation o)
    {
        var v = new List<string>();
        var s = o.Request.Stream;
        if (o.Request.ScheduledUs < 0) v.Add("ScheduledUs < 0");
        if (o.StartedUs is { } st && st < o.Request.ScheduledUs) v.Add("StartedUs < ScheduledUs");
        if (o.EndedUs is { } en && (o.StartedUs is not { } st2 || en < st2)) v.Add("EndedUs without valid StartedUs");
        if (o.DurationUs is < 0) v.Add("DurationUs < 0");
        if ((o.DurationUs is null) != (o.TimingSource is null)) v.Add("DurationUs/TimingSource mismatch");
        if (o.Status == Success && (o.EndedUs is null || o.DurationUs is null)) v.Add("Success without timing");
        if (NoDurationStatuses.Contains(o.Status) && o.DurationUs is not null) v.Add($"{o.Status} with duration");
        if (o.Status == ProbeStatus.Skipped && (o.StartedUs is not null || o.ErrorCode is null)) v.Add("Skipped needs reason and no start");
        if ((s.Kind == ProbeKind.Https) != (s.Family == RequestFamily.Any)) v.Add("Https <=> Any family");
        if (o.ObservedFamily is { } f && !s.Family.Matches(f)) v.Add("ObservedFamily differs from RequestFamily");
        if (HttpsOnly.Contains(o.Status) && s.Kind != ProbeKind.Https) v.Add($"{o.Status} outside Https");
        if (o.Status == DnsFailure && s.Kind is not (ProbeKind.SystemDns or ProbeKind.Https)) v.Add("DnsFailure outside DNS/Https");
        if (o.Attribution == Attribution.Proxied && s.Kind != ProbeKind.Https) v.Add("Proxied outside Https");
        if (s.Kind == ProbeKind.Icmp && o.Attribution is not (Attribution.RoutePredicted or Attribution.Unknown)) v.Add("Icmp attribution");
        if (o.ErrorCode is { Length: > 64 }) v.Add("ErrorCode > 64 chars");
        return v;
    }
}
