using System.Diagnostics;
using System.Runtime.InteropServices;
using global::Windows.Networking.Connectivity;

namespace ConnectionClue.Windows;

/// <summary>Traffic of one app. Path is the executable; it is empty for Windows system services.</summary>
public sealed record AppUsage(string Name, string Path, ulong Sent, ulong Received);

/// <summary>
/// Per-app traffic from Windows' own data usage accounting (the source of Settings > Network &amp; internet > Data usage).
/// Works as a standard user without elevation. Windows keeps about one-minute buckets, so results describe the time
/// around the check. This app is left out, so its own probes and speed test never appear.
/// </summary>
public static partial class AppNetworkUsage
{
    public static async Task<IReadOnlyList<AppUsage>> TopAsync(DateTimeOffset from, DateTimeOffset to, int count = 3)
    {
        try
        {
            if (NetworkInformation.GetInternetConnectionProfile() is not { } profile) return [];
            var states = new NetworkUsageStates { Roaming = TriStates.DoNotCare, Shared = TriStates.DoNotCare };
            var usage = await profile.GetAttributedNetworkUsageAsync(from, to, states);
            string self = Path.GetFileName(Environment.ProcessPath ?? "");
            return [.. usage
                .Where(u => !Path.GetFileName(u.AttributionId).Equals(self, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(u => u.BytesSent + u.BytesReceived)
                .Take(count)
                .Select(u =>
                {
                    string path = DosPath(u.AttributionId);
                    return new AppUsage(Name(u.AttributionName, path), path, u.BytesSent, u.BytesReceived);
                })];
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            return []; // attribution is a bonus; the counters still show the traffic
        }
    }

    /// <summary>Store apps report a display name; desktop apps report "\device\harddiskvolumeN\…", mapped to a drive letter.</summary>
    private static string DosPath(string attributionId)
    {
        foreach (var (device, drive) in Volumes.Value)
            if (attributionId.StartsWith(device + @"\", StringComparison.OrdinalIgnoreCase))
                return drive + attributionId[device.Length..];
        return attributionId;
    }

    private static string Name(string attributionName, string path)
    {
        if (!string.IsNullOrWhiteSpace(attributionName)) return attributionName.Trim();
        if (path.Length == 0) return "";
        try
        {
            if (File.Exists(path) && FileVersionInfo.GetVersionInfo(path) is { } info)
            {
                var name = (info.FileDescription ?? info.ProductName)?.Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static readonly Lazy<List<(string Device, string Drive)>> Volumes = new(() =>
    {
        var map = new List<(string, string)>();
        var buffer = new char[512];
        foreach (var drive in DriveInfo.GetDrives())
        {
            string letter = drive.Name.TrimEnd('\\');
            uint n = QueryDosDevice(letter, buffer, (uint)buffer.Length);
            if (n > 0) map.Add((new string(buffer, 0, (int)n).Split('\0')[0], letter));
        }
        return map;
    });

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint QueryDosDevice(string deviceName, [Out] char[] targetPath, uint max);
}
