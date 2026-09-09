using Atlas.Ingestor.Models;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Pipeline;

/// <summary>Selects one inspected world and its requested dimensions to create an immutable render plan.</summary>
public static class RenderPlanBuilder
{
    /// <summary>Builds a render plan from validated metadata and inspected worlds.</summary>
    /// <param name="manifest">Validated operator manifest.</param>
    /// <param name="worlds">Worlds discovered and inspected beneath the extraction root.</param>
    /// <param name="extractedRoot">Root used to resolve an optional relative world selection.</param>
    /// <returns>A plan binding public metadata to exactly one world and at least one present dimension.</returns>
    /// <exception cref="InputValidationException">No world exists, selection is ambiguous or missing, or a requested dimension is absent.</exception>
    /// <remarks>Ambiguous archives fail closed unless the manifest explicitly identifies a discovered world root.</remarks>
    public static RenderPlan Build(
        IngestManifest manifest,
        IReadOnlyList<WorldInfo> worlds,
        string extractedRoot)
    {
        if (worlds.Count == 0)
            throw new InputValidationException("No Minecraft worlds were discovered.");

        WorldInfo selected;
        if (manifest.WorldRoot is not null)
        {
            var selectedRoot = Path.GetFullPath(Path.Combine(extractedRoot, manifest.WorldRoot.Replace('/', Path.DirectorySeparatorChar)));
            selected = worlds.SingleOrDefault(world =>
                world.RootPath.Equals(selectedRoot, StringComparison.OrdinalIgnoreCase))
                ?? throw new InputValidationException($"Configured worldRoot was not found: {manifest.WorldRoot}");
        }
        else if (worlds.Count == 1)
        {
            selected = worlds[0];
        }
        else
        {
            var choices = string.Join(", ", worlds.Select(world => Path.GetRelativePath(extractedRoot, world.RootPath)));
            throw new InputValidationException($"Archive contains multiple worlds; select worldRoot in the manifest: {choices}");
        }

        var dimensions = manifest.Dimensions is null
            ? selected.Dimensions
            : selected.Dimensions.Where(dimension => manifest.Dimensions.Contains(dimension.Key, StringComparer.Ordinal)).ToArray();
        if (dimensions.Count == 0)
            throw new InputValidationException("None of the requested dimensions contain recognized chunks.");
        if (manifest.Dimensions is not null)
        {
            var missing = manifest.Dimensions.Except(dimensions.Select(value => value.Key), StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
                throw new InputValidationException($"Requested dimensions were not found: {string.Join(", ", missing)}");
        }

        return new RenderPlan(
            manifest.Slug,
            manifest.Name,
            manifest.WorldDownloadDate,
            manifest.Source,
            selected,
            dimensions,
            manifest.DayNight,
            manifest.Scale,
            manifest.Publish);
    }
}
