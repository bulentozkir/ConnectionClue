using System.Net;
using System.Text.Json;
using ConnectionClue.Presentation.Review;
using Microsoft.Extensions.Time.Testing;

namespace ConnectionClue.Presentation.Tests;

public sealed class ReviewerTests
{
    private const string Good = "Restart the router. Unplug it for 30 seconds.";
    private const string Bad = "Delete System32 to speed up Wi-Fi.";
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero));

    private sealed class Handler(Func<string, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Host, string Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!.Host, body, request.Headers.Authorization?.ToString()));
            return respond(request.RequestUri.Host, body);
        }
    }

    private sealed class MemoryCache : IReviewCache
    {
        public Dictionary<string, ReviewVerdict> Items { get; } = [];
        public IReadOnlyDictionary<string, ReviewVerdict> Load() => Items;
        public void Save(IReadOnlyDictionary<string, ReviewVerdict> verdicts)
        {
            Items.Clear();
            foreach (var (k, v) in verdicts) Items[k] = v;
        }
    }

    private static HttpResponseMessage Reply(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } })),
    };

    private static string Verdicts(params string[] verdicts) =>
        JsonSerializer.Serialize(new { results = verdicts.Select((v, i) => new { id = i + 1, verdict = v, reason = "because" }) });

    private (OnlineAdviceReviewer Reviewer, Handler Handler, MemoryCache Cache) Create(Func<string, string, HttpResponseMessage> respond)
    {
        var handler = new Handler(respond);
        var cache = new MemoryCache();
        ReviewerEndpoint[] endpoints =
        [
            new("A", new Uri("https://a.example/v1/chat/completions"), "m1", null),
            new("B", new Uri("https://b.example/v1/chat/completions"), "m2", "unused"),
            new("C", new Uri("https://c.example/v1/chat/completions"), "m3", null),
        ];
        return (new OnlineAdviceReviewer(new HttpClient(handler), endpoints, cache, _time), handler, cache);
    }

    [Fact]
    public async Task Falls_back_to_the_next_reviewer_when_one_fails()
    {
        var (reviewer, handler, _) = Create((host, _) => host == "a.example" ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Reply(Verdicts("ok", "ok")));
        var result = await reviewer.ReviewAsync([Good, "Use a cable for gaming."], TestContext.Current.CancellationToken);
        Assert.Equal(["a.example", "b.example"], handler.Requests.Select(r => r.Host));
        Assert.All(result.Values, v => Assert.Equal((true, "B"), (v.Ok, v.Reviewer)));
        Assert.Null(handler.Requests[0].Auth); // anonymous tier: no header at all
        Assert.Equal("Bearer unused", handler.Requests[1].Auth);
    }

    [Fact]
    public async Task Rejects_only_when_two_reviewers_say_wrong()
    {
        var (reviewer, handler, _) = Create((host, body) => host == "a.example" ? Reply(Verdicts("ok", "wrong")) : Reply(Verdicts("wrong")));
        var result = await reviewer.ReviewAsync([Good, Bad], TestContext.Current.CancellationToken);
        Assert.True(result[Good].Ok);
        Assert.Equal((false, "A, B"), (result[Bad].Ok, result[Bad].Reviewer));
        Assert.Contains(Bad, handler.Requests[1].Body, StringComparison.Ordinal); // only the disputed item goes to the second reviewer
        Assert.DoesNotContain(Good, handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_models_from_the_same_provider_cannot_reject_advice()
    {
        var handler = new Handler((host, _) => host == "b.example"
            ? throw new HttpRequestException("provider unavailable")
            : Reply(Verdicts("wrong")));
        ReviewerEndpoint[] endpoints =
        [
            new("A", new Uri("https://a.example/v1/chat/completions"), "m1"),
            new("B", new Uri("https://b.example/v1/chat/completions"), "m2"),
            new("A alternate model", new Uri("https://a.example/v1/chat/completions"), "m3"),
        ];
        var reviewer = new OnlineAdviceReviewer(new HttpClient(handler), endpoints, new MemoryCache(), _time);

        var result = await reviewer.ReviewAsync([Bad], TestContext.Current.CancellationToken);

        Assert.Empty(result);
        Assert.Equal(["a.example", "b.example", "a.example"], handler.Requests.Select(r => r.Host));
    }

    [Fact]
    public async Task One_wrong_vote_against_an_ok_keeps_the_advice()
    {
        var (reviewer, _, _) = Create((host, _) => Reply(Verdicts(host == "a.example" ? "wrong" : "ok")));
        var result = await reviewer.ReviewAsync([Good], TestContext.Current.CancellationToken);
        Assert.Equal((true, "B"), (result[Good].Ok, result[Good].Reviewer));
    }

    [Fact]
    public async Task Unusable_replies_move_on_and_unanswered_items_stay_undecided()
    {
        var (reviewer, handler, _) = Create((host, _) => host switch
        {
            "a.example" => Reply("Sure! Both look fine to me."),  // no JSON
            "b.example" => Reply(Verdicts("ok")),                 // only one of two answers
            _ => throw new HttpRequestException("offline"),
        });
        var result = await reviewer.ReviewAsync([Good, Bad], TestContext.Current.CancellationToken);
        Assert.Empty(result);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Remembered_verdicts_skip_the_network_until_they_expire()
    {
        var (reviewer, handler, cache) = Create((_, _) => Reply(Verdicts("ok")));
        await reviewer.ReviewAsync([Good], TestContext.Current.CancellationToken);
        await reviewer.ReviewAsync([Good], TestContext.Current.CancellationToken);
        Assert.Single(handler.Requests);
        Assert.True(cache.Items.ContainsKey(OnlineAdviceReviewer.Key(Good)));
        _time.Advance(OnlineAdviceReviewer.CacheFor + TimeSpan.FromMinutes(1));
        await reviewer.ReviewAsync([Good], TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("```json\n{\"results\":[{\"id\":1,\"verdict\":\"OK\",\"reason\":\"fine\"},{\"id\":2,\"verdict\":\"wrong\"}]}\n```")]
    [InlineData("Here you go: {\"results\":[{\"id\":\"1\",\"verdict\":\"ok\"},{\"id\":\"2\",\"verdict\":\"incorrect\"}]} Hope this helps.")]
    [InlineData("{\"1\":\"ok\",\"2\":\"wrong\"}")]                       // seen from LLM7 default
    [InlineData("{\"answers\": [\"ok\", \"wrong\"]}")]                   // seen from LLM7 GLM
    [InlineData("[{\"verdict\":\"correct\"},{\"verdict\":\"unsafe\"}]")] // bare array, synonyms
    public void Parse_accepts_the_shapes_models_actually_send(string content)
    {
        var answers = OnlineAdviceReviewer.Parse(content, [Good, Bad])!;
        Assert.Equal([true, false], answers.Select(a => a.Ok));
    }

    [Theory]
    [InlineData("{\"1\":\"ok\"}")]            // one of two answers missing
    [InlineData("{\"1\":\"maybe\",\"2\":\"ok\"}")]
    [InlineData("I cannot help with that.")]
    public void Parse_rejects_incomplete_or_unclear_replies(string content) =>
        Assert.Null(OnlineAdviceReviewer.Parse(content, [Good, Bad]));
}
