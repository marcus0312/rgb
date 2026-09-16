using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace UnifiedRgb.Core.Services;

/// <summary>
/// Registers / unregisters Unified RGB for Windows logon via HKCU\...\Run.
/// No-ops on non-Windows platforms.
/// </summary>
public static class AutostartService
{
    public const string RunValueName = "UnifiedRgb";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool IsEnabled()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            return IsEnabledWindows();
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled, bool startMinimized = false)
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (enabled)
            EnableWindows(startMinimized);
        else
            DisableWindows();
    }

    /// <summary>Executable path used for the Run key (quotes + optional --minimized).</summary>
    public static string GetLaunchCommand(bool startMinimized = false)
    {
        var exe = ResolveExePath();
        var cmd = $"\"{exe}\"";
        if (startMinimized)
            cmd += " --minimized";
        return cmd;
    }

    private static string ResolveExePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return path;

        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.Combine(dir, "UnifiedRgb.App.exe");
        if (File.Exists(candidate))
            return candidate;

        return Path.Combine(dir, "UnifiedRgb.App.dll");
    }

    [SupportedOSPlatform("windows")]
    private static bool IsEnabledWindows()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var value = key?.GetValue(RunValueName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    [SupportedOSPlatform("windows")]
    private static void EnableWindows(bool startMinimized)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(RunValueName, GetLaunchCommand(startMinimized));
    }

    [SupportedOSPlatform("windows")]
    private static void DisableWindows()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}
