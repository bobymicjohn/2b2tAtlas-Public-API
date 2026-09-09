namespace Atlas;

/// <summary>
/// Atlas post-processing applied after uNmINeD's native night renderer. uNmINeD supplies the
/// authoritative block-light luminance; the paired day tile supplies terrain colour that native
/// night mode otherwise desaturates almost completely.
/// </summary>
public sealed class NightColorGradeOptions
{
    /// <summary>Gets or sets whether colour-preserving night grading is enabled.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the multiplier applied to daytime chroma, from 0 (gray) to 2.</summary>
    public double Saturation { get; set; } = 1.2;

    /// <summary>Gets or sets the multiplier applied to uNmINeD night luminance.</summary>
    public double Lightness { get; set; } = 1.15;
}

/// <summary>Dependency-free per-pixel night colour grading shared by previews and the worker.</summary>
public static class NightColorGrade
{
    /// <summary>Combines day chroma with native night luminance while preserving night alpha.</summary>
    public static (byte R, byte G, byte B) Apply(
        byte dayR, byte dayG, byte dayB,
        byte nightR, byte nightG, byte nightB,
        double saturation, double lightness)
    {
        var dayLuminance = Luminance(dayR, dayG, dayB);
        var nativeNightLuminance = Luminance(nightR, nightG, nightB);
        var target = Math.Clamp(nativeNightLuminance * lightness, 0, 255);
        if (dayLuminance < 1)
        {
            return (Clamp(nightR * lightness), Clamp(nightG * lightness), Clamp(nightB * lightness));
        }

        var scale = target / dayLuminance;
        return (
            Clamp(target + (dayR * scale - target) * saturation),
            Clamp(target + (dayG * scale - target) * saturation),
            Clamp(target + (dayB * scale - target) * saturation));
    }

    private static double Luminance(byte red, byte green, byte blue) =>
        red * 0.2126 + green * 0.7152 + blue * 0.0722;

    private static byte Clamp(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
