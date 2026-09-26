using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;
using ConnectionClue.Presentation.ViewModels;

namespace ConnectionClue.App;

/// <summary>
/// Opens the Settings pages and tools that recommendations point to, and copies text. Only the app's own fixed targets
/// are accepted (ms-settings pages, four system tools, Intel's driver tool page, and the router's private IPv4 address),
/// so saved or tampered data can never launch anything else.
/// </summary>
internal sealed class Shell : IShell
{
    private static readonly string[] Tools = ["devmgmt.msc", "taskmgr", "powercfg.cpl", "ncpa.cpl", MainViewModel.IntelDriverPage];

    public void Open(string target)
    {
        if (!Allowed(target)) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // The user cancelled a prompt, or the tool is unavailable (for example on a locked-down PC).
        }
    }

    public void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (COMException) { } // another app holds the clipboard
    }

    /// <summary>The router's address, when it is a private IPv4 address that can host an admin page.</summary>
    public static string? RouterAddress(IPAddress? gateway) => gateway is { AddressFamily: AddressFamily.InterNetwork } g && IsPrivate(g) ? g.ToString() : null;

    private static bool Allowed(string target) =>
        target.StartsWith("ms-settings:", StringComparison.Ordinal) && target.Length < 64 && !target.Contains(' ')
        || Tools.Contains(target)
        || Uri.TryCreate(target, UriKind.Absolute, out var release) && release.Scheme == Uri.UriSchemeHttps
            && release.Host == "github.com" && release.AbsolutePath.StartsWith("/bulentozkir/ConnectionClue/releases/", StringComparison.Ordinal)
            && release.Query.Length == 0 && release.Fragment.Length == 0
        || Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.AbsolutePath == "/"
            && IPAddress.TryParse(uri.Host, out var ip) && RouterAddress(ip) is not null;

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
    }
}
