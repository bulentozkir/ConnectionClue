using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using ConnectionClue.Core;

namespace ConnectionClue.Windows.Probes;

/// <summary>
/// "Cold fetch" HTTPS check: fresh connection per check, default certificate validation, system proxy honoured,
/// no redirects, cookies, credentials or decompression, body capped at 4 KiB.
/// </summary>
public sealed class HttpsProbe(SessionClock clock, ITargetResolver targets, IWebProxy? proxy = null) : IProbe, IDisposable
{
    private readonly IWebProxy _proxy = proxy ?? HttpClient.DefaultProxy;
    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new();

    public ProbeKind Kind => ProbeKind.Https;

    public async Task<ProbeObservation> ExecuteAsync(ProbeRequest request, CancellationToken ct)
    {
        if (request.Stream.Family != RequestFamily.Any) return Observations.Skipped(request, "FamilyMustBeAny");
        var target = targets.Resolve(request.Stream);
        if (target?.Host is not { Length: > 0 } host) return Observations.Skipped(request, "NoName");
        var endpoint = _endpoints.GetOrAdd(target.TargetId, _ => new Endpoint(target));
        if (Interlocked.Exchange(ref endpoint.Busy, 1) == 1) return Observations.Skipped(request, "StreamBusy");
        try
        {
            var uri = new UriBuilder(Uri.UriSchemeHttps, host, target.Port, target.Path).Uri;
            return await SendAsync(request, target, uri, endpoint, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref endpoint.Busy, 0);
        }
    }

    /// <summary>Drop handlers on a segment change so the next check starts from a clean proxy/path context.</summary>
    public void ResetConnections()
    {
        foreach (var e in _endpoints.Values) e.Dispose();
        _endpoints.Clear();
    }

    public void Dispose() => ResetConnections();

    private async Task<ProbeObservation> SendAsync(ProbeRequest request, ResolvedTarget target, Uri uri, Endpoint ep, CancellationToken ct)
    {
        ep.BeginCheck(!_proxy.IsBypassed(uri) && _proxy.GetProxy(uri) is { } p && p != uri);
        var attribution = ep.Proxied ? Attribution.Proxied : Attribution.SocketObserved;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(request.Timeout);
        using var message = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        message.Headers.ConnectionClose = true;
        message.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        message.Headers.UserAgent.ParseAdd("ConnectionClue/1.0");

        long started = clock.NowUs;
        try
        {
            using var response = await ep.Invoker(_proxy).SendAsync(message, timeout.Token).ConfigureAwait(false);
            var (bytes, oversize, matched) = await ReadBodyAsync(response, target.BodyToken, timeout.Token).ConfigureAwait(false);
            long ended = clock.NowUs;
            int code = (int)response.StatusCode;
            var (status, error) = ProbeClassification.FromHttp(code, target.ExpectedStatus, oversize, matched);
            var detail = new HttpsDetail(code, bytes,
                code is >= 300 and < 400 && response.Headers.Location is { IsAbsoluteUri: true } location ? location.Host : null,
                ep.Issuer, target.IsExpectedIssuer(ep.Issuer), RetryAfter(response));
            return new ProbeObservation(request, status, attribution, ep.Family, started, ended, ended - started,
                TimingSource.UserMode, error, detail);
        }
        catch (OperationCanceledException)
        {
            return Failure(ct.IsCancellationRequested ? ProbeStatus.Cancelled : ProbeStatus.Timeout, null);
        }
        catch (HttpRequestException ex)
        {
            var (status, error) = Classify(ex, ep);
            return Failure(status, error);
        }

        ProbeObservation Failure(ProbeStatus status, string? error) =>
            Observations.NoDuration(request, status, attribution, ep.Family, started, clock.NowUs, error,
                new HttpsDetail(null, 0, null, ep.Issuer, target.IsExpectedIssuer(ep.Issuer), null));
    }

    private static async Task<(int Bytes, bool Oversize, bool Matched)> ReadBodyAsync(
        HttpResponseMessage response, string? token, CancellationToken ct)
    {
        var buffer = new byte[ProbeClassification.MaxBodyBytes + 1];
        int total = 0;
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        int kept = Math.Min(total, ProbeClassification.MaxBodyBytes);
        bool matched = token is null || buffer.AsSpan(0, kept).IndexOf(Encoding.UTF8.GetBytes(token)) >= 0;
        return (kept, total > ProbeClassification.MaxBodyBytes, matched);
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter is not { } r ? null : r.Delta ?? (r.Date - clock.Time.GetUtcNow());

    private static (ProbeStatus, string?) Classify(HttpRequestException ex, Endpoint ep)
    {
        if (ep.PinViolation) return (ProbeStatus.Unsupported, "AddressNotPinned");
        if (ex.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) return (ProbeStatus.ProxyAuthRequired, null);
        var socket = Inner<SocketException>(ex);
        return ex.HttpRequestError switch
        {
            HttpRequestError.SecureConnectionError =>
                (ProbeStatus.TlsFailure, Inner<AuthenticationException>(ex) is null ? "Handshake" : "Validation"),
            HttpRequestError.ProxyTunnelError => (ProbeStatus.HttpUnexpected, "ProxyTunnel"),
            HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded => (ProbeStatus.HttpUnexpected, "InvalidResponse"),
            _ when socket is not null => ProbeClassification.FromSocketError(socket.SocketErrorCode),
            HttpRequestError.NameResolutionError => (ProbeStatus.DnsFailure, "NameResolution"),
            _ => (ProbeStatus.InternalError, ex.HttpRequestError.ToString()),
        };
    }

    private static T? Inner<T>(Exception ex) where T : Exception
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
            if (e is T match) return match;
        return null;
    }

    /// <summary>Per-target handler state. At most one check is in flight per target, so per-check fields are safe.</summary>
    private sealed class Endpoint(ResolvedTarget target) : IDisposable
    {
        private HttpMessageInvoker? _invoker;
        public int Busy;
        public string? Issuer;
        public IpFamily? Family;
        public bool PinViolation;
        public bool Proxied;

        public void BeginCheck(bool proxied) => (Issuer, Family, PinViolation, Proxied) = (null, null, false, proxied);

        public HttpMessageInvoker Invoker(IWebProxy proxy) => _invoker ??= new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            UseProxy = true,
            Proxy = proxy,
            PooledConnectionLifetime = TimeSpan.Zero,
            MaxResponseHeadersLength = 16,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    Issuer = certificate?.Issuer;
                    return errors == SslPolicyErrors.None; // default validation, only observed
                },
            },
            ConnectCallback = ConnectAsync,
        }, disposeHandler: true);

        private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                var remote = ((IPEndPoint)socket.RemoteEndPoint!).Address;
                if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
                Family = IpFamilies.Of(remote);
                if (!Proxied && !target.IsPinned(remote))
                {
                    PinViolation = true;
                    throw new IOException("AddressNotPinned");
                }
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public void Dispose() => _invoker?.Dispose();
    }
}
