namespace ConnectionClue.Core;

/// <summary>A local, read-only configuration inventory. Missing sources are explicit; absent properties are unknown.
/// Raw values stay local and are never used as online advice-review input.</summary>
public sealed record NetworkConfiguration(
    IReadOnlyList<NetworkConfigurationSource> Sources,
    bool? DhcpEnabled = null,
    bool? StaticDns = null,
    bool? LinkLocalAddressOnly = null)
{
    public IReadOnlyList<string> Unavailable => [.. Sources.Where(s => s.Error is not null).Select(s => s.Name)];
}

public sealed record NetworkConfigurationSource(string Name, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows,
    string? Error = null);
