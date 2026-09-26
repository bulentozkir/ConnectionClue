using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ConnectionClue.Presentation.Review;

/// <summary>One online LLM with an OpenAI-compatible chat completions endpoint. Key is null for anonymous free tiers.</summary>
public sealed record ReviewerEndpoint(string Name, Uri Endpoint, string Model, string? Key = null, int TimeoutSeconds = 20);

/// <summary>Outcome for one advice text: Ok (confirmed), or rejected by two independent reviewers.</summary>
public sealed record ReviewVerdict(bool Ok, string Reviewer, string Reason, DateTimeOffset CheckedAtUtc);

/// <summary>Remembered verdicts, keyed by a hash of the generic advice text.</summary>
public interface IReviewCache
{
    IReadOnlyDictionary<string, ReviewVerdict> Load();
    void Save(IReadOnlyDictionary<string, ReviewVerdict> verdicts);
}

public interface IAdviceReviewer
{
    /// <summary>Verdicts by text for the texts that could be decided; texts no reviewer could answer are missing.</summary>
    Task<IReadOnlyDictionary<string, ReviewVerdict>> ReviewAsync(IReadOnlyList<string> texts, CancellationToken ct);
}

/// <summary>
/// Checks generic advice texts with free online LLMs before they are shown. Texts must already be redacted (names and
/// measurements replaced); only generic advice is in the request body, though providers still receive normal HTTPS
/// connection metadata such as the public IP. Stable text lets each wording be checked once and cached. Reviewers are tried
/// in order: a timeout, HTTP error or unusable reply moves on to the next one. An item is
/// rejected only when two distinct providers call it wrong, so correlated models from one service cannot hide correct advice.
/// </summary>
public sealed class OnlineAdviceReviewer(HttpClient http, IReadOnlyList<ReviewerEndpoint> reviewers, IReviewCache cache, TimeProvider time)
    : IAdviceReviewer
{
    public static readonly TimeSpan CacheFor = TimeSpan.FromDays(30);

    private const string Instructions =
        "You review troubleshooting advice that a Windows 11 network diagnostics app shows to home users. Names from the user's PC " +
        "are replaced by [name] and measured numbers by N. For each numbered item answer \"ok\" if the advice is technically correct, " +
        "safe and possible on Windows 11 or a typical home router, or \"wrong\" if it is incorrect, unsafe, or refers to settings that " +
        "do not exist. Reply with JSON only, no other text: {\"results\":[{\"id\":1,\"verdict\":\"ok\",\"reason\":\"at most 12 words\"}]}";

    public async Task<IReadOnlyDictionary<string, ReviewVerdict>> ReviewAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var cached = cache.Load().Where(p => now - p.Value.CheckedAtUtc < CacheFor).ToDictionary(p => p.Key, p => p.Value);
        var decided = new Dictionary<string, ReviewVerdict>();
        var open = new List<string>();
        foreach (var text in texts.Distinct())
        {
            if (cached.TryGetValue(Key(text), out var verdict)) decided[text] = verdict;
            else open.Add(text);
        }

        var firstNo = new Dictionary<string, (string Reviewer, string Provider, string Reason)>();
        foreach (var reviewer in reviewers)
        {
            if (open.Count == 0) break;
            if (await AskAsync(reviewer, open, ct) is not { } answers) continue; // no answer: try the next reviewer
            foreach (var (text, ok, reason) in answers)
            {
                if (ok) decided[text] = new(true, reviewer.Name, reason, now);
                else if (firstNo.TryGetValue(text, out var first)
                    && !first.Provider.Equals(reviewer.Endpoint.Host, StringComparison.OrdinalIgnoreCase))
                {
                    firstNo.Remove(text);
                    decided[text] = new(false, $"{first.Reviewer}, {reviewer.Name}", $"{first.Reason}; {reason}", now);
                }
                else if (!firstNo.ContainsKey(text))
                    firstNo[text] = (reviewer.Name, reviewer.Endpoint.Host, reason);
            }
            open = [.. open.Where(t => !decided.ContainsKey(t))];
        }

        foreach (var (text, verdict) in decided) cached[Key(text)] = verdict;
        cache.Save(cached);
        return decided;
    }

    /// <summary>Stable cache key for a generic text.</summary>
    public static string Key(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private async Task<List<(string Text, bool Ok, string Reason)>?> AskAsync(ReviewerEndpoint reviewer, List<string> texts, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(reviewer.TimeoutSeconds));
        try
        {
            var body = new JsonObject
            {
                ["model"] = reviewer.Model,
                ["temperature"] = 0,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = Instructions },
                    new JsonObject { ["role"] = "user", ["content"] = string.Join("\n", texts.Select((t, i) => $"{i + 1}. {t}")) },
                },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, reviewer.Endpoint)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (reviewer.Key is { Length: > 0 } key) request.Headers.Authorization = new("Bearer", key); // anonymous tiers take no header
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return Parse(reply?["choices"]?[0]?["message"]?["content"]?.GetValue<string>(), texts);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException
            or FormatException or NotSupportedException)
        {
            if (ct.IsCancellationRequested) throw;
            return null;
        }
    }

    /// <summary>
    /// Reads the verdicts from a reply. Models rarely follow a schema exactly, so the common shapes are accepted:
    /// {"results":[{"id":1,"verdict":"ok"}]}, a single array under any name, {"1":"ok"}, or a bare array, even inside
    /// code fences or prose, with synonyms such as correct/incorrect. Every item must be answered, or the reply is unusable.
    /// </summary>
    public static List<(string Text, bool Ok, string Reason)>? Parse(string? content, IReadOnlyList<string> texts)
    {
        if (Json(content) is not { } node) return null;
        var answers = new Dictionary<int, (bool, string)>();
        void Add(int n, JsonNode? value)
        {
            if (n < 1 || n > texts.Count || answers.ContainsKey(n)) return;
            string? verdict = value is JsonObject o ? Text(o["verdict"] ?? o["answer"] ?? o["result"] ?? o["status"]) : Text(value);
            bool? ok = verdict?.Trim().ToLowerInvariant() switch
            {
                "ok" or "correct" or "valid" or "yes" or "true" or "safe" => true,
                "wrong" or "incorrect" or "invalid" or "no" or "false" or "unsafe" => false,
                _ => null,
            };
            if (ok is { } v) answers[n] = (v, value is JsonObject r ? Text(r["reason"] ?? r["explanation"]) ?? "" : "");
        }
        void Read(JsonArray items)
        {
            for (int i = 0; i < items.Count; i++) Add(Id(items[i]) ?? i + 1, items[i]);
        }
        switch (node)
        {
            case JsonArray items: Read(items); break;
            case JsonObject o when o["results"] is JsonArray items: Read(items); break;
            case JsonObject o when o.Count == 1 && o.First().Value is JsonArray items: Read(items); break;
            case JsonObject o:
                foreach (var (key, value) in o)
                    if (int.TryParse(key, out int n)) Add(n, value);
                break;
        }
        return answers.Count == texts.Count ? [.. answers.OrderBy(a => a.Key).Select(a => (texts[a.Key - 1], a.Value.Item1, a.Value.Item2))] : null;
    }

    private static JsonNode? Json(string? content)
    {
        if (content is null) return null;
        int brace = content.IndexOf('{'), bracket = content.IndexOf('[');
        bool array = bracket >= 0 && (brace < 0 || bracket < brace);
        int start = array ? bracket : brace, end = content.LastIndexOf(array ? ']' : '}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(content[start..(end + 1)]); }
        catch (JsonException) { return null; }
    }

    private static int? Id(JsonNode? item) => item is JsonObject o && o["id"] is JsonValue id
        ? id.TryGetValue(out int n) ? n : id.TryGetValue(out string? s) && int.TryParse(s, out var parsed) ? parsed : null
        : null;

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s
        : node is JsonValue b && b.TryGetValue(out bool flag) ? (flag ? "true" : "false") : null;
}
