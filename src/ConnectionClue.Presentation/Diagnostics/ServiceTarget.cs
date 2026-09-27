using System.Globalization;
using System.Net;

namespace ConnectionClue.Presentation.Diagnostics;

public sealed record ServiceTarget(string Host, int Port);

public enum ServiceTargetStatus { Connected, TimedOut, Refused, NameLookupFailed, Unreachable }

public sealed record ServiceTargetResult(ServiceTarget Target, ServiceTargetStatus Status, double? ElapsedMilliseconds);

/// <summary>A well-known public service endpoint; Name is a brand, so it is not translated.</summary>
public sealed record NamedServiceTarget(string Name, ServiceTarget Target);

/// <summary>Median connect time of a few attempts; Status is Connected when any attempt connected.</summary>
public sealed record ServiceTestResult(NamedServiceTarget Service, ServiceTargetStatus Status, double? MedianMilliseconds);

/// <summary>
/// Real services per "What's happening?" choice: game platforms for gaming lag, call relays for choppy calls, streaming
/// front ends for buffering, and connectivity checks for disconnects. TCP connect time only: no application data is sent,
/// and it does not measure a game's UDP traffic, call quality or a stream's bitrate.
/// </summary>
public static class SymptomServices
{
    public static IReadOnlyList<NamedServiceTarget> For(ConnectionClue.Analysis.Symptom symptom) => symptom switch
    {
        ConnectionClue.Analysis.Symptom.Gaming =>
        [
            new("Xbox network", new("user.auth.xboxlive.com", 443)),
            new("Steam", new("api.steampowered.com", 443)),
            new("Epic Games", new("account-public-service-prod.ol.epicgames.com", 443)),
        ],
        ConnectionClue.Analysis.Symptom.Calls =>
        [
            new("Microsoft Teams", new("worldaz.tr.teams.microsoft.com", 443)),
            new("Zoom", new("zoom.us", 443)),
            new("Google Meet", new("meet.google.com", 443)),
        ],
        ConnectionClue.Analysis.Symptom.Video =>
        [
            new("Netflix", new("www.netflix.com", 443)),
            new("YouTube", new("www.youtube.com", 443)),
            new("Twitch", new("www.twitch.tv", 443)),
        ],
        _ =>
        [
            new("Windows connectivity check", new("www.msftconnecttest.com", 80)),
            new("Cloudflare", new("one.one.one.one", 443)),
            new("Google", new("dns.google", 443)),
        ],
    };

    /// <summary>
    /// The target each symptom's box starts with in Settings: a popular service besides the built-in ones (Riot Games for
    /// gaming, Discord for calls, Prime Video for video, Microsoft for disconnections). Users can replace or clear it.
    /// </summary>
    public static NamedServiceTarget DefaultTarget(ConnectionClue.Analysis.Symptom symptom) => symptom switch
    {
        ConnectionClue.Analysis.Symptom.Gaming => new("Riot Games", new("auth.riotgames.com", 443)),
        ConnectionClue.Analysis.Symptom.Calls => new("Discord", new("discord.com", 443)),
        ConnectionClue.Analysis.Symptom.Video => new("Prime Video", new("www.primevideo.com", 443)),
        _ => new("Microsoft", new("www.microsoft.com", 443)),
    };

    public static string DefaultTargetText(ConnectionClue.Analysis.Symptom symptom) =>
        DefaultTarget(symptom).Target is var t ? $"{t.Host}:{t.Port}" : "";

    /// <summary>Tests all services in parallel, a few attempts each; a failed name lookup is not retried.</summary>
    public static async Task<IReadOnlyList<ServiceTestResult>> TestAsync(IServiceTargetProbe probe,
        IReadOnlyList<NamedServiceTarget> services, CancellationToken cancellationToken, int attempts = 3) =>
        await Task.WhenAll(services.Select(async service =>
        {
            var times = new List<double>(attempts);
            var last = ServiceTargetStatus.Unreachable;
            for (int i = 0; i < attempts; i++)
            {
                ServiceTargetResult result;
                try { result = await probe.ProbeAsync(service.Target, cancellationToken).ConfigureAwait(false); }
                catch (System.Net.Sockets.SocketException) { result = new(service.Target, ServiceTargetStatus.Unreachable, null); }
                last = result.Status;
                if (result is { Status: ServiceTargetStatus.Connected, ElapsedMilliseconds: { } ms }) times.Add(ms);
                else if (result.Status == ServiceTargetStatus.NameLookupFailed) break;
            }
            times.Sort();
            return new ServiceTestResult(service, times.Count > 0 ? ServiceTargetStatus.Connected : last,
                times.Count > 0 ? times[times.Count / 2] : null);
        })).ConfigureAwait(false);
}

public interface IServiceTargetProbe
{
    Task<ServiceTargetResult> ProbeAsync(ServiceTarget target, CancellationToken cancellationToken);
}

public static class ServiceTargetParser
{
    public static bool TryParse(string? text, out ServiceTarget? target)
    {
        target = null;
        string input = text?.Trim() ?? "";
        string host;
        string portText;
        if (input.Length > 0 && input[0] == '[')
        {
            int end = input.IndexOf("]:", StringComparison.Ordinal);
            if (end < 2 || !IPAddress.TryParse(input[1..end], out var address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
            host = address.ToString();
            portText = input[(end + 2)..];
        }
        else
        {
            int colon = input.LastIndexOf(':');
            if (colon <= 0 || input.AsSpan(0, colon).Contains(':')) return false;
            host = input[..colon].Trim();
            portText = input[(colon + 1)..];
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            return false;
        if (IPAddress.TryParse(host, out var ip))
        {
            target = new(ip.ToString(), port);
            return true;
        }
        if (host.Length > 253) return false;
        try
        {
            string ascii = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(host.TrimEnd('.'));
            if (Uri.CheckHostName(ascii) != UriHostNameType.Dns || ascii.Split('.').Any(label => label.Length is 0 or > 63))
                return false;
            target = new(ascii, port);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
