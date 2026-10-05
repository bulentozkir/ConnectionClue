using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using ConnectionClue.Core;

namespace ConnectionClue.Windows.Probes;

/// <summary>
/// HTTP throughput: the budget's parallel transfers for a bounded time and byte count; ramp-up excluded by
/// <see cref="ThroughputMath"/>. A light budget (one connection) checks its rate as the bytes move and stops as soon as it
/// settles, and reports a lower bound when its byte cap ends it first. The download URL template takes the requested byte
/// count as {0}. System proxy honoured; no cookies, credentials, redirects or decompression. Upload payload is random, so
/// compression cannot inflate results.
/// </summary>
public sealed class ThroughputProbe : IThroughputProbe, IDisposable
{
    private const long DownloadChunk = 25_000_000, UploadChunk = 10_000_000;
    private const double SettleSampleSeconds = 0.002;
    private static readonly TimeSpan LiveEvery = TimeSpan.FromMilliseconds(200);
    private static readonly byte[] Payload = RandomNumberGenerator.GetBytes(64 * 1024);
    private readonly TimeProvider _time;
    private readonly HttpClient _client;
    private readonly string _downloadUrl;
    private readonly Uri _uploadUrl;
    private readonly long _maxDownloadBytes, _maxUploadBytes;

    /// <summary>maxDownloadBytes and maxUploadBytes cap every phase on top of its budget.</summary>
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
            MaxConnectionsPerServer = ThroughputBudget.FullStreams,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("ConnectionClue/1.0");
    }

    public async Task<ThroughputResult> MeasureAsync(ThroughputDirection direction, ThroughputBudget budget, IProgress<double>? liveMbps, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(budget.Duration);
        long cap = Math.Min(budget.MaxBytes, direction == ThroughputDirection.Download ? _maxDownloadBytes : _maxUploadBytes), total = 0;
        long start = _time.GetTimestamp();
        var samples = new List<(double Seconds, long Bytes)> { (0, 0) };
        // A settling phase records the bytes as they move (every few milliseconds), so it stops moments after settling.
        var moved = budget.StopWhenSettled ? new List<(double Seconds, long Bytes)> { (0, 0) } : null;
        double? settled = null;

        void Count(int bytes)
        {
            long counted = Interlocked.Add(ref total, bytes);
            if (moved is not null) Settle(counted);
            if (counted >= cap) stop.Cancel();
        }

        void Settle(long counted)
        {
            double now = _time.GetElapsedTime(start).TotalSeconds;
            lock (moved!)
            {
                if (settled is not null || now - moved[^1].Seconds < SettleSampleSeconds) return;
                moved.Add((now, counted));
                settled = ThroughputMath.SettledMbps(moved);
                if (settled is null) return;
            }
            stop.Cancel();
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

        var workers = Enumerable.Range(0, Math.Max(1, budget.Streams)).Select(_ => WorkerAsync()).ToArray();
        var all = Task.WhenAll(workers);
        var reported = samples[0];
        using (var sampler = new PeriodicTimer(TimeSpan.FromMilliseconds(250), _time))
        {
            while (!all.IsCompleted)
            {
                await Task.WhenAny(sampler.WaitForNextTickAsync().AsTask(), all);
                var now = (_time.GetElapsedTime(start).TotalSeconds, Interlocked.Read(ref total));
                samples.Add(now);
                if (now.Item1 - reported.Seconds >= LiveEvery.TotalSeconds)
                {
                    liveMbps?.Report((now.Item2 - reported.Bytes) * 8 / 1e6 / (now.Item1 - reported.Seconds));
                    reported = now;
                }
            }
        }
        try { await all; }
        catch (Exception) when (all.IsFaulted || all.IsCanceled) { } // inspected per worker below

        var elapsed = _time.GetElapsedTime(start);
        var end = (elapsed.TotalSeconds, Interlocked.Read(ref total));
        samples.Add(end);
        if (ct.IsCancellationRequested) return new(direction, ProbeStatus.Cancelled, null, total, elapsed);
        if (moved is not null)
        {
            lock (moved)
            {
                if (settled is { } steady) return new(direction, ProbeStatus.Success, steady, total, elapsed);
                moved.Add(end);
                samples = [.. moved]; // finer than the timer's samples
            }
        }

        var failure = workers.Where(w => w.IsFaulted).Select(w => w.Exception!.InnerException).FirstOrDefault(e => e is not OperationCanceledException);
        if (total == 0 || failure is not null && ThroughputMath.Mbps(samples) is null)
        {
            string code = failure is HttpRequestException { StatusCode: { } status } ? $"Http{(int)status}"
                : failure is HttpRequestException h ? h.HttpRequestError.ToString()
                : failure?.GetType().Name ?? "NoData";
            return new(direction, ProbeStatus.NetworkUnreachable, null, total, elapsed, code);
        }
        return new(direction, ProbeStatus.Success, ThroughputMath.Mbps(samples), total, elapsed,
            LowerBound: budget.StopWhenSettled && total >= cap);
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
