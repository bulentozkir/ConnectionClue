using System.Net.Http.Json;
using System.Text.Json;

namespace ConnectionClue.Presentation.Updates;

public sealed record UpdateCheckResult(string CurrentVersion, string LatestVersion, bool IsUpdateAvailable, Uri? ReleaseUri);

public interface IUpdateChecker
{
    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>Checks the public GitHub release API only when the user asks; it sends no device or network details.</summary>
public sealed class ReleaseUpdateChecker(HttpClient httpClient, Version currentVersion) : IUpdateChecker
{
    public static readonly Uri LatestReleaseEndpoint = new("https://api.github.com/repos/bulentozkir/ConnectionClue/releases/latest");
    private static readonly Uri ReleaseRoot = new("https://github.com/bulentozkir/ConnectionClue/releases/");

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(LatestReleaseEndpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? throw new InvalidDataException("Release tag is missing.");
        string urlText = root.GetProperty("html_url").GetString() ?? throw new InvalidDataException("Release URL is missing.");
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            throw new InvalidDataException("Release tag is not a numeric version.");
        if (!Uri.TryCreate(urlText, UriKind.Absolute, out var releaseUri) ||
            releaseUri.Scheme != Uri.UriSchemeHttps || releaseUri.Host != ReleaseRoot.Host ||
            !releaseUri.AbsolutePath.StartsWith(ReleaseRoot.AbsolutePath, StringComparison.Ordinal))
            throw new InvalidDataException("Release URL is outside the official repository.");
        return new(currentVersion.ToString(), tag, latest > currentVersion, releaseUri);
    }
}
