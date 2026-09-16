namespace UnifiedRgb.Core.Models;

/// <summary>
/// Persisted app settings under %LocalAppData%/UnifiedRgb/settings.json
/// </summary>
public sealed class AppSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 6742;

    /// <summary>Register HKCU Run / Startup shortcut so the app launches at login.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>When true, main window starts hidden (tray only).</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Closing the window hides to tray instead of exiting.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Last profile name successfully applied / selected.</summary>
    public string? LastProfileName { get; set; }

    /// <summary>After connecting on launch, auto-apply <see cref="LastProfileName"/>.</summary>
    public bool AutoApplyOnLaunch { get; set; } = true;
}
