using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace ConnectionClue.App;

internal static class StartupManager
{
    private const string TaskId = "ConnectionClueStartup";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "ConnectionClue";
    private const int AppModelErrorNoPackage = 15700;

    public static async Task<bool> SetEnabledAsync(bool enabled, string executablePath)
    {
        if (IsPackaged())
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (!enabled)
            {
                task.Disable();
                return task.State == StartupTaskState.Enabled;
            }
            return await task.RequestEnableAsync() == StartupTaskState.Enabled;
        }

        using var run = Registry.CurrentUser.CreateSubKey(RunPath, writable: true)
            ?? throw new IOException("The current-user startup registry key could not be opened.");
        if (enabled)
            run.SetValue(RunValue, $"\"{executablePath}\" --startup", RegistryValueKind.String);
        else
            run.DeleteValue(RunValue, throwOnMissingValue: false);
        return enabled;
    }

    public static async Task<bool> IsEnabledAsync(string executablePath)
    {
        if (IsPackaged())
            return (await StartupTask.GetAsync(TaskId)).State == StartupTaskState.Enabled;

        using var run = Registry.CurrentUser.OpenSubKey(RunPath, writable: false);
        string expected = $"\"{executablePath}\" --startup";
        return string.Equals(run?.GetValue(RunValue) as string, expected, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPackaged()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    public static bool IsStoreManaged() =>
        IsPackaged() && Package.Current.SignatureKind == PackageSignatureKind.Store;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, StringBuilder? packageFullName);
}
