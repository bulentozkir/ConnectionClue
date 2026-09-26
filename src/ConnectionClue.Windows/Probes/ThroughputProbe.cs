using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using ConnectionClue.Core;

namespace ConnectionClue.Windows.Probes;

/// <summary>
/// HTTP throughput: <see cref="Streams"/> parallel transfers for a fixed time with a byte cap; ramp-up excluded by
/// <see cref="ThroughputMath"/>. The download URL template takes the requested byte count as {0}. System proxy honoured;
/// no cookies, credentials, redirects or decompression. Upload payload is random, so compression cannot inflate results.
/// </summary>
public sealed class ThroughputProbe : IThroughputProbe, IDisposable
{
    public const int Streams = 4;
    private const long DownloadChunk = 25_000_000, UploadChunk = 10_000_000;
    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(64 * 1024);
    private readonly TimeProvider _time;
    private readonly HttpClient _client;
    private readonly string _downloadUrl;
    private readonly Uri _uploadUrl;
    private readonly long _maxDownloadBytes, _maxUploadBytes;

    public ThroughputProbe(TimeProvider time, string downloadUrlTemplate, Uri uploadUrl,
        long maxDownloadBytes = 200_000_000, long maxUploadBytes = 100_000_000, IWebProxy? proxy = null)
    {
        (_time, _downloadUrl, _uploadUrl, _maxDownloadBytes, _maxUploadBytes) = (time, downloadUrlTemplate, uploadUrl, maxDownloadBytes, maxUploadBytes);
        _client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            UseProxy = true,
            Proxy = proxy ?? HttpClient.DefaultProxy,
            MaxConnectionsPerServer = Streams,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ConnectionClue/1.0");
    }

    public async Task<ThroughputResult> MeasureAsync(ThroughputDirection direction, TimeSpan duration, IProgress<double>? liveMbps, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(duration);
        long cap = direction == ThroughputDirection.Download ? _maxDownloadBytes : _maxUploadBytes, total = 0;
        long start = _time.GetTimestamp();
        var samples = new List<(double Seconds, long Bytes)> { (0, 0) };

        void Count(int bytes)
        {
            if (Interlocked.Add(ref total, bytes) >= cap) stop.Cancel();
        }

        async Task WorkerAsync()
        {
            var buffer = new byte[81920];
            while (!stop.IsCancellationRequested)
            {
                if (direction == ThroughputDirection.Download)
                {
                    var url = string.Format(CultureInfo.InvariantCulture, _downloadUrl, Math.Min(DownloadChunk, cap));
                    using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stop.Token);
                    response.EnsureSuccessStatusCode();
                    await using var body = await response.Content.ReadAsStreamAsync(stop.Token);
                    int n;
                    while ((n = await body.ReadAsync(buffer, stop.Token)) > 0) Count(n);
                }
                else
                {
                    using var content = new PayloadContent(Math.Min(UploadChunk, cap), Count);
                    using var response = await _client.PostAsync(_uploadUrl, content, stop.Token);
                    response.EnsureSuccessStatusCode();
                }
            }
        }

        var workers = Enumerable.Range(0, Streams).Select(_ => WorkerAsync()).ToArray();
        var all = Task.WhenAll(workers);
        using (var sampler = new PeriodicTimer(TimeSpan.FromMilliseconds(250), _time))
        {
            while (!all.IsCompleted)
            {
                await Task.WhenAny(sampler.WaitForNextTickAsync().AsTask(), all);
                var now = (_time.GetElapsedTime(start).TotalSeconds, Interlocked.Read(ref total));
                var previous = samples[^1];
                samples.Add(now);
                if (now.Item1 > previous.Seconds) liveMbps?.Report((now.Item2 - previous.Bytes) * 8 / 1e6 / (now.Item1 - previous.Seconds));
            }
        }
        try { await all; }
        catch (Exception) when (all.IsFaulted || all.IsCanceled) { } // inspected per worker below

        var elapsed = _time.GetElapsedTime(start);
        samples.Add((elapsed.TotalSeconds, Interlocked.Read(ref total)));
        if (ct.IsCancellationRequested) return new(direction, ProbeStatus.Cancelled, null, total, elapsed);

        var failure = workers.Where(w => w.IsFaulted).Select(w => w.Exception!.InnerException).FirstOrDefault(e => e is not OperationCanceledException);
        if (total == 0 || failure is not null && ThroughputMath.Mbps(samples) is null)
        {
            string code = failure is HttpRequestException { StatusCode: { } status } ? $"Http{(int)status}"
                : failure is HttpRequestException h ? h.HttpRequestError.ToString()
                : failure?.GetType().Name ?? "NoData";
            return new(direction, ProbeStatus.NetworkUnreachable, null, total, elapsed, code);
        }
        return new(direction, ProbeStatus.Success, ThroughputMath.Mbps(samples), total, elapsed);
    }

    public void Dispose() => _client.Dispose();

    /// <summary>Streams a fixed-length random body and reports every write.</summary>
    private sealed class PayloadContent(long length, Action<int> written) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (long left = length; left > 0;)
            {
                int n = (int)Math.Min(Payload.Length, left);
                await stream.WriteAsync(Payload.AsMemory(0, n), cancellationToken);
                written(n);
                left -= n;
            }
        }

        protected override bool TryComputeLength(out long size)
        {
            size = length;
            return true;
        }
    }
}
