using System.Diagnostics;
using System.Text;

namespace UnifiedRgb.Core.Services;

/// <summary>
/// Detects common vendor RGB apps that fight OpenRGB for device access.
/// </summary>
public static class VendorConflictDetector
{
    /// <summary>
    /// Process name (without .exe) → friendly label for the banner / status.
    /// </summary>
    private static readonly (string ProcessName, string Label)[] KnownConflicts =
    [
        ("lghub", "Logitech G HUB"),
        ("lghub_agent", "Logitech G HUB agent"),
        ("lghub_updater", "Logitech G HUB updater"),
        ("MasterPlus", "Cooler Master MasterPlus+"),
        ("MPIV", "Cooler Master MasterPlus+"),
        ("Polychrome", "ASRock Polychrome Sync"),
        ("PolychromeRGB", "ASRock Polychrome Sync"),
        ("XtremeTuner", "GALAX / KFA2 Xtreme Tuner"),
        ("GALAXRGB", "GALAX RGB"),
        ("GalaxRGB", "GALAX RGB"),
    ];

    public sealed record Conflict(string ProcessName, string Label, int Pid);

    public static IReadOnlyList<Conflict> DetectRunning()
    {
        var found = new List<Conflict>();
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return found;
        }

        try
        {
            foreach (var proc in processes)
            {
                string? name = null;
                try
                {
                    name = proc.ProcessName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                foreach (var (processName, label) in KnownConflicts)
                {
                    if (!name.Equals(processName, StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains(processName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!seenLabels.Add(label))
                        continue;

                    int pid = 0;
                    try { pid = proc.Id; } catch { /* ignore */ }
                    found.Add(new Conflict(name, label, pid));
                    break;
                }
            }
        }
        finally
        {
            foreach (var p in processes)
            {
                try { p.Dispose(); } catch { /* ignore */ }
            }
        }

        return found;
    }

    public static string? FormatWarning(IReadOnlyList<Conflict> conflicts)
    {
        if (conflicts.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.Append("Vendor RGB conflict: ");
        sb.Append(string.Join(", ", conflicts.Select(c => c.Label).Distinct()));
        sb.Append(" still running — quit them so OpenRGB can claim devices. See README “Vendor conflict runbook”.");
        return sb.ToString();
    }
}
