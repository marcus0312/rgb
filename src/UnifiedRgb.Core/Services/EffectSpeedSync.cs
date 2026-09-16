namespace UnifiedRgb.Core.Services;

/// <summary>
/// Shared speed tiers for Sync all / Apply so CM HID and OpenRGB devices get a
/// consistent "feel" rather than raw 0–1 mapped independently into mismatched ranges.
/// Sync matches <b>tiers</b>, not perfect hardware clocks — see README.
/// </summary>
public static class EffectSpeedSync
{
    /// <summary>Discrete Slow→Fast tiers (matches CM HID 0x00–0x04).</summary>
    public const int TierCount = 5;

    public enum DeviceKind
    {
        CoolerMasterHid,
        OpenRgb
    }

    /// <summary>Map UI speed 0–1 to a tier index 0..(TierCount-1).</summary>
    public static int ToTier(double speed01)
    {
        speed01 = Math.Clamp(speed01, 0, 1);
        return (int)Math.Round(speed01 * (TierCount - 1));
    }

    /// <summary>Map UI 0–100 to tier.</summary>
    public static int ToTierFromUi(double speed0To100) =>
        ToTier(Math.Clamp(speed0To100, 0, 100) / 100.0);

    /// <summary>
    /// CM Gen2 HID: tier 0→0x00 … tier 4→0x04 (higher byte = faster, per OpenRGB CMARGBGen2A1).
    /// Mid UI (~50) → tier 2 → 0x02 (SPEED_HALF).
    /// </summary>
    public static byte MapCmHid(double speed01) =>
        (byte)Math.Clamp(ToTier(speed01), 0, CmArgbGen2HidController.SpeedMax);

    /// <summary>
    /// OpenRGB: map tier into the mode's SpeedMin→SpeedMax range.
    /// Does <b>not</b> swap min/max — OpenRGB often uses inverted ranges
    /// (e.g. ASRock Polychrome: SpeedMin=0xFF slow, SpeedMax=0x00 fast).
    /// speed01=0 → SpeedMin (slow end), speed01=1 → SpeedMax (fast end).
    /// </summary>
    public static uint MapOpenRgb(uint speedMin, uint speedMax, bool supportsSpeed, uint currentSpeed, double speed01)
    {
        if (!supportsSpeed)
            return currentSpeed;

        if (speedMin == speedMax)
            return speedMin;

        var tier = ToTier(speed01);
        var t = tier / (double)(TierCount - 1);
        // Allow inverted ranges: SpeedMax may be less than SpeedMin.
        var value = speedMin + t * ((double)speedMax - speedMin);
        return (uint)Math.Round(value);
    }

    /// <summary>Convenience for OpenRGB.NET <c>Mode</c> fields.</summary>
    public static uint MapOpenRgbMode(uint speedMin, uint speedMax, bool supportsSpeed, uint currentSpeed, double speed01) =>
        MapOpenRgb(speedMin, speedMax, supportsSpeed, currentSpeed, speed01);

    /// <summary>
    /// Unified entry: CM HID → byte in low 8 bits; OpenRGB → full uint.
    /// </summary>
    public static uint Map(DeviceKind kind, double speed01, uint speedMin = 0, uint speedMax = 0, bool supportsSpeed = true, uint currentSpeed = 0) =>
        kind switch
        {
            DeviceKind.CoolerMasterHid => MapCmHid(speed01),
            _ => MapOpenRgb(speedMin, speedMax, supportsSpeed, currentSpeed, speed01)
        };

    public static string DescribeTier() =>
        "UI 0–100 → 5 tiers (0=slow … 4=fast). CM HID: tier→0x00–0x04. " +
        "OpenRGB: tier→SpeedMin…SpeedMax (inverted ranges preserved: min=slow, max=fast).";
}
