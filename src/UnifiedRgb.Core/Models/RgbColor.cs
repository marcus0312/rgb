namespace UnifiedRgb.Core.Models;

/// <summary>Simple RGB color (0–255) used by profiles and the UI layer.</summary>
public readonly record struct RgbColor(byte R, byte G, byte B)
{
    /// <summary>
    /// Parse <c>#RGB</c>, <c>#RRGGBB</c>, or <c>RRGGBB</c>. On failure returns red (legacy).
    /// Prefer <see cref="TryFromHex"/> for live typing.
    /// </summary>
    public static RgbColor FromHex(string hex) =>
        TryFromHex(hex, out var c) ? c : new RgbColor(255, 0, 0);

    /// <summary>
    /// Parse <c>#RGB</c> / <c>#RRGGBB</c> / <c>RRGGBB</c> (optional leading <c>#</c>).
    /// Whitespace is trimmed; returns false when the text is empty or invalid.
    /// </summary>
    public static bool TryFromHex(string? hex, out RgbColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
            return false;

        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            // #RGB → #RRGGBB
            hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
        }

        if (hex.Length != 6)
            return false;

        if (byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            color = new RgbColor(r, g, b);
            return true;
        }

        return false;
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
