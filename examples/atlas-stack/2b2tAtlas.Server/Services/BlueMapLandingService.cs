using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Services;

/// <summary>Resolves a viewer arrival from the exact dated render, without modifying its WDL or meshes.</summary>
public sealed class BlueMapLandingService(AtlasContext context)
{
    /// <summary>Loads only the public render's own warp and catalog location.</summary>
    public async Task<BlueMapLanding?> FindAsync(int renderId, CancellationToken cancellationToken = default)
    {
        var render = await context.Renders.AsNoTracking()
            .Include(r => r.ArchiveWarp).Include(r => r.LocationRow)
            .SingleOrDefaultAsync(r => r.Id == renderId && r.IsPublic == 1, cancellationToken);
        return render is null ? null : Resolve(render);
    }

    /// <summary>Selects exact Archive coordinates or an explicitly identified in-bounds fallback.</summary>
    public static BlueMapLanding? Resolve(Render render)
    {
        bool Inside(double? x, double? z) => x.HasValue && z.HasValue &&
            double.IsFinite(x.Value) && double.IsFinite(z.Value) &&
            render.MinX.HasValue && render.MinZ.HasValue &&
            render.MaxXExclusive.HasValue && render.MaxZExclusive.HasValue &&
            x >= render.MinX && x < render.MaxXExclusive &&
            z >= render.MinZ && z < render.MaxZExclusive;
        static double? Height(double? y) => y.HasValue && double.IsFinite(y.Value) ? y : null;

        // Never borrow a different warp or date, round decimals, or scale dimensions.
        var warp = render.ArchiveWarp;
        if (warp is not null && Inside(warp.ArchiveX, warp.ArchiveZ))
        {
            return new(warp.ArchiveX!.Value, Height(warp.ArchiveY), warp.ArchiveZ!.Value,
                Height(warp.ArchiveY) is null ? "warp-terrain-height" : "archive-warp");
        }

        var location = render.LocationRow;
        if (location is not null && location.Dimension == render.Dimension && Inside(location.X, location.Z))
        {
            return new(location.X, Height(location.Y), location.Z, "catalog-fallback");
        }

        if (render.MinX.HasValue && render.MinZ.HasValue &&
            render.MaxXExclusive > render.MinX && render.MaxZExclusive > render.MinZ)
        {
            return new(((double)render.MinX.Value + render.MaxXExclusive!.Value) / 2,
                null, ((double)render.MinZ.Value + render.MaxZExclusive!.Value) / 2, "footprint-fallback");
        }

        return null;
    }
}

/// <summary>Native-dimension arrival; null Y requests terrain height from BlueMap.</summary>
public sealed record BlueMapLanding(double X, double? Y, double Z, string Source);
