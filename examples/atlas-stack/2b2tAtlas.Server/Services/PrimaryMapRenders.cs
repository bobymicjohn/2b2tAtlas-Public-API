namespace _2b2tAtlas.Server.Services;

using Atlas;

/// <summary>
/// The canonical set of dimension-level <b>primary</b> map layers exposed by the
/// external render catalog (<c>GET /api/maprenders/catalog</c>). These mirror the
/// base layers the client stacks in its picker (atlas-map.js DIMENSIONS). This is a
/// read-only view for third-party consumers; it does not drive the app's own picker.
/// </summary>
public static class PrimaryMapRenders
{
    private const string TileBase = "https://tiles.atlas.example/AtlasTiles";

    /// <summary>Gets the full-map primary render descriptors, ordered by dimension then picker order.</summary>
    public static IReadOnlyList<RenderCatalogEntry> All { get; } =
    [
        // --- Overworld (0) — hosted AtlasTiles pyramids (day/night) ---
        Tiles("256k", "256k (2021)", 0, "256k", $"{TileBase}/Overworld/256k/{{dn}}/{{z}}/{{y}}/{{x}}.png"),
        Tiles("100k2025", "100k (2025)", 0, "100k", $"{TileBase}/Overworld/100k(256k)/2025/{{dn}}/{{z}}/{{y}}/{{x}}.png"),
        Tiles("100k", "100k Spawn (late 2018; released 2019)", 0, "100k", $"{TileBase}/Overworld/100k(256k)/{{dn}}/{{z}}/{{y}}/{{x}}.png"),
        Tiles("7kY255", "7k (Y255)", 0, "7k", $"{TileBase}/Overworld/7k(256k)/Y255/{{dn}}/{{z}}/{{y}}/{{x}}.png"),
        Tiles("7kY254", "7k (Y254)", 0, "7k", $"{TileBase}/Overworld/7k(256k)/Y254/{{dn}}/{{z}}/{{y}}/{{x}}.png"),
        Tiles("OwO", "OwO", 0, "256k", $"{TileBase}/Overworld/OwO(256k)/{{dn}}/{{z}}/{{y}}/{{x}}.png"),
        Tiles("Pekora", "Pekora", 0, "256k", $"{TileBase}/Overworld/Pekora(256k)/{{dn}}/{{z}}/{{y}}/{{x}}.png"),
        Tiles("TGG", "TGG", 0, "256k", $"{TileBase}/Overworld/TGG(256k)/{{dn}}/{{z}}/{{y}}/{{x}}.png"),
        Place("place-world", "1m World (2026)", 0, "1m"),
        Place("place-obsidian", "1m Obsidian (2026)", 0, "1m"),

        // --- Nether (1) ---
        // Capture dates from the original 2b2t_100k_final_v1.3 README (l_amp).
        Tiles("43k", "43k Nether (Aug 15–17, 2019)", 1, "43k", $"{TileBase}/Nether/43k/7/{{z}}/{{y}}/{{x}}.png",
            dayNight: false, maxNativeZoom: 9, coordinateScheme: "atlas-nether-legacy-v1"),
        // 5k is preserved on disk but has no verified world-coordinate transform.
        Place("place-world", "100k World (2026)", 1, "100k"),

        // --- End (2) ---
        Tiles("42k", "42k End (Aug–Sep 2019)", 2, "42k", $"{TileBase}/End/42k/{{z}}/{{y}}/{{x}}.png", dayNight: false, maxNativeZoom: 9),
        Place("place-world", "256k World (2026)", 2, "256k"),
    ];

    private static RenderCatalogEntry Tiles(
        string slug, string name, int dimension, string scale, string urlTemplate,
        bool dayNight = true, int? maxNativeZoom = null, string coordinateScheme = "xyz-v1") => new()
    {
        Kind = "primary",
        Id = slug,
        Name = name,
        Dimension = dimension,
        Scale = scale,
        TileUrlTemplate = urlTemplate,
        HasDayNight = dayNight,
        MaxNativeZoom = maxNativeZoom,
        CoordinateScheme = coordinateScheme,
        Source = "2b2t Atlas",
    };

    // 2b2t.place layers are resampled/proxied, not a plain {z}/{y}/{x} pyramid.
    private static RenderCatalogEntry Place(string slug, string name, int dimension, string scale) => new()
    {
        Kind = "primary",
        Id = slug,
        Name = name,
        Dimension = dimension,
        Scale = scale,
        TileUrlTemplate = null,
        HasDayNight = false,
        CoordinateScheme = "place-proxy",
        Source = "2b2t.place",
    };
}
