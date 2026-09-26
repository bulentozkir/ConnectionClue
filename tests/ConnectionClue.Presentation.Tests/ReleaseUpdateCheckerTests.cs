using System.Net;
using System.Text;
using ConnectionClue.Presentation.Updates;

namespace ConnectionClue.Presentation.Tests;

public sealed class ReleaseUpdateCheckerTests
{
    [Fact]
    public async Task Reports_a_newer_official_release()
    {
        using var client = Client("""{"tag_name":"v1.0.3","html_url":"https://github.com/bulentozkir/ConnectionClue/releases/tag/v1.0.3"}""");
        var checker = new ReleaseUpdateChecker(client, new Version(1, 0, 2));

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("v1.0.3", result.LatestVersion);
        Assert.Equal("https://github.com/bulentozkir/ConnectionClue/releases/tag/v1.0.3", result.ReleaseUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Does_not_offer_the_current_or_an_older_release()
    {
        using var client = Client("""{"tag_name":"1.0.2","html_url":"https://github.com/bulentozkir/ConnectionClue/releases/tag/1.0.2"}""");
        var result = await new ReleaseUpdateChecker(client, new Version(1, 0, 2)).CheckAsync(TestContext.Current.CancellationToken);
        Assert.False(result.IsUpdateAvailable);
    }

    [Theory]
    [InlineData("""{"tag_name":"bad","html_url":"https://github.com/bulentozkir/ConnectionClue/releases/tag/bad"}""")]
    [InlineData("""{"tag_name":"v1.0.3","html_url":"https://example.com/"}""")]
    public async Task Rejects_invalid_or_non_official_release_metadata(string json)
    {
        using var client = Client(json);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ReleaseUpdateChecker(client, new Version(1, 0, 2)).CheckAsync(TestContext.Current.CancellationToken));
    }

    private static HttpClient Client(string json) =>
        new(new StubHandler(json)) { Timeout = TimeSpan.FromSeconds(3) };

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(ReleaseUpdateChecker.LatestReleaseEndpoint, request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
