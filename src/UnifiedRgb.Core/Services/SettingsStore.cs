using System.Text.Json;
using UnifiedRgb.Core.Models;

namespace UnifiedRgb.Core.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public SettingsStore(string? directory = null)
    {
        var dir = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnifiedRgb");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        DirectoryPath = dir;
    }

    public string DirectoryPath { get; }
    public string FilePath => _path;

    public AppSettings Load()
    {
        if (!File.Exists(_path))
            return CreateDefaults();

        try
        {
            var json = File.ReadAllText(_path);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return settings ?? CreateDefaults();
        }
        catch
        {
            return CreateDefaults();
        }
    }

    public void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_path, json);
    }

    /// <summary>
    /// True when running from the LocalAppData Programs install location
    /// (or UNIFIED_RGB_INSTALLED=1), so Start with Windows defaults ON.
    /// </summary>
    public static bool IsInstalledBuild()
    {
        var flag = Environment.GetEnvironmentVariable("UNIFIED_RGB_INSTALLED");
        if (string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var installedRoot = Path.Combine(local, "Programs", "UnifiedRgb");
            var full = Path.GetFullPath(exe);
            var root = Path.GetFullPath(installedRoot);
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;

            // Installer writes this marker next to the exe.
            var marker = Path.Combine(AppContext.BaseDirectory, "installed.marker");
            return File.Exists(marker);
        }
        catch
        {
            return false;
        }
    }

    private static AppSettings CreateDefaults() => new()
    {
        StartWithWindows = IsInstalledBuild(),
        CloseToTray = true,
        AutoApplyOnLaunch = true
    };
}
