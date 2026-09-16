namespace UnifiedRgb.Core.Models;

/// <summary>Simple RGB color (0–255) used by profiles and the UI layer.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    public static RgbColor FromHex(string hex)
    {
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6 &&
            byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return new RgbColor(r, g, b);
        }

        return new RgbColor(255, 0, 0);
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public RgbColor WithBrightness(double brightness01)
    {
        brightness01 = Math.Clamp(brightness01, 0, 1);
        return new RgbColor(
            (byte)Math.Round(R * brightness01),
            (byte)Math.Round(G * brightness01),
            (byte)Math.Round(B * brightness01));
    }
}
