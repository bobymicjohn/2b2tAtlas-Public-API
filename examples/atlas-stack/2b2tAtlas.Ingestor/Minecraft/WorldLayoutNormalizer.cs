using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Minecraft;

/// <summary>
/// Normalizes canonical vanilla namespaced dimension exports inside the private extracted scratch tree
/// to the legacy paths understood by the pinned renderer. The immutable ZIP snapshot is never changed.
/// </summary>
public static class WorldLayoutNormalizer
{
    private const int RendererFallbackDataVersion = 4189; // Minecraft 1.21.4, the pinned renderer's registry baseline.
    /// <summary>Normalizes one extracted world root, failing closed on conflicting storage.</summary>
    public static void NormalizeCanonicalNamespacedDimensions(
        string rootPath,
        IReadOnlyList<string>? requestedDimensions = null)
    {
        var root = Path.GetFullPath(rootPath);
        NormalizeOverworld(root);
        NormalizeWholeDimension(root, "dimensions/minecraft/the_nether", "DIM-1", "nether");
        NormalizeWholeDimension(root, "dimensions/minecraft/the_end", "DIM1", "end");
        NormalizeSingleCustomDimensionOverride(root, requestedDimensions);
    }

    /// <summary>
    /// Creates minimal renderer-only level metadata when a legacy WDL omitted both level.dat files.
    /// This runs only in the extracted scratch tree after inspection; the archived ZIP remains byte-identical.
    /// </summary>
    public static void EnsureRendererLevelDat(string rootPath, string? levelName = null)
    {
        var root = Path.GetFullPath(rootPath);
        var levelDat = Contained(root, Path.Combine(root, "level.dat"));
        if (File.Exists(levelDat)) return;
        var backup = Contained(root, Path.Combine(root, "level.dat_old"));
        if (File.Exists(backup))
        {
            File.Copy(backup, levelDat, overwrite: false);
            return;
        }

        using var output = new FileStream(levelDat, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var gzip = new GZipStream(output, CompressionLevel.SmallestSize);
        gzip.WriteByte(10); // root compound
        WriteNbtString(gzip, string.Empty);
        gzip.WriteByte(10); // Data compound
        WriteNbtString(gzip, "Data");
        WriteNbtInt(gzip, "DataVersion", RendererFallbackDataVersion);
        WriteNbtStringTag(gzip, "LevelName", string.IsNullOrWhiteSpace(levelName) ? "Recovered WDL" : levelName);
        gzip.WriteByte(0); // end Data
        gzip.WriteByte(0); // end root
    }

    private static void WriteNbtInt(Stream output, string name, int value)
    {
        output.WriteByte(3);
        WriteNbtString(output, name);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        output.Write(bytes);
    }

    private static void WriteNbtStringTag(Stream output, string name, string value)
    {
        output.WriteByte(8);
        WriteNbtString(output, name);
        WriteNbtString(output, value);
    }

    private static void WriteNbtString(Stream output, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        output.Write(length);
        output.Write(bytes);
    }

    /// <summary>
    /// Treats an explicit single-dimension request as the operator's original-dimension declaration for a
    /// legacy/custom export. This only runs when the requested canonical storage is absent and exactly one
    /// custom dimension contains chunks, so auto-detect never guesses and multi-world ambiguity fails closed.
    /// </summary>
    private static void NormalizeSingleCustomDimensionOverride(string root, IReadOnlyList<string>? requestedDimensions)
    {
        var requested = requestedDimensions?
            .Where(value => value is "overworld" or "nether" or "end")
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (requested.Length != 1 || HasCanonicalStorage(root, requested[0])) return;

        var customRoots = FindCustomDimensionRoots(root).ToArray();
        if (customRoots.Length == 0) return;
        if (customRoots.Length > 1)
        {
            var choices = string.Join(", ", customRoots.Select(path =>
                Path.GetRelativePath(root, path).Replace('\\', '/')));
            throw new InputValidationException(
                $"The requested {requested[0]} is absent and the world contains multiple custom dimensions: {choices}. " +
                "Export one dimension per WDL or normalize the intended source before ingestion.");
        }

        var source = customRoots[0];
        switch (requested[0])
        {
            case "overworld":
                MoveStorageChildren(root, source, "custom overworld");
                break;
            case "nether":
                MoveCustomWholeDimension(root, source, "DIM-1", "nether");
                break;
            case "end":
                MoveCustomWholeDimension(root, source, "DIM1", "end");
                break;
        }
    }

    private static bool HasCanonicalStorage(string root, string dimension) => dimension switch
    {
        "overworld" => HasChunkStorage(root),
        "nether" => HasChunkStorage(Path.Combine(root, "DIM-1")),
        "end" => HasChunkStorage(Path.Combine(root, "DIM1")),
        _ => false,
    };

    private static IEnumerable<string> FindCustomDimensionRoots(string root)
    {
        var dimensionsRoot = Path.Combine(root, "dimensions");
        if (!Directory.Exists(dimensionsRoot)) yield break;
        var found = 0;
        foreach (var region in Directory.EnumerateDirectories(dimensionsRoot, "region", SearchOption.AllDirectories))
        {
            if (++found > 128)
                throw new InputValidationException("World contains more than 128 custom dimension candidates.");
            var dimensionRoot = Contained(root, Path.GetDirectoryName(region)!);
            var relative = Path.GetRelativePath(dimensionsRoot, dimensionRoot).Replace('\\', '/');
            if (relative is "minecraft/overworld" or "minecraft/the_nether" or "minecraft/the_end")
                continue;
            if (Directory.EnumerateFiles(region, "r.*.*.mca", SearchOption.TopDirectoryOnly).Any() ||
                Directory.EnumerateFiles(region, "r.*.*.mcr", SearchOption.TopDirectoryOnly).Any())
                yield return dimensionRoot;
        }
    }

    private static bool HasChunkStorage(string dimensionRoot)
    {
        var region = Path.Combine(dimensionRoot, "region");
        return Directory.Exists(region) &&
            (Directory.EnumerateFiles(region, "r.*.*.mca", SearchOption.TopDirectoryOnly).Any() ||
             Directory.EnumerateFiles(region, "r.*.*.mcr", SearchOption.TopDirectoryOnly).Any());
    }

    private static void MoveStorageChildren(string root, string source, string label)
    {
        foreach (var storageName in new[] { "region", "entities", "poi" })
        {
            var sourceStorage = Contained(root, Path.Combine(source, storageName));
            if (!Directory.Exists(sourceStorage)) continue;
            var targetStorage = Contained(root, Path.Combine(root, storageName));
            if (Directory.Exists(targetStorage) || File.Exists(targetStorage))
                throw new InputValidationException(
                    $"{label} conflicts with existing root storage '{storageName}'. Normalize one source before ingestion.");
            Directory.Move(sourceStorage, targetStorage);
        }
    }

    private static void MoveCustomWholeDimension(string root, string source, string targetRelative, string label)
    {
        var target = Contained(root, Path.Combine(root, targetRelative));
        if (Directory.Exists(target) || File.Exists(target))
            throw new InputValidationException(
                $"Custom {label} conflicts with existing '{targetRelative}' storage. Normalize one source before ingestion.");
        Directory.Move(source, target);
    }

    private static void NormalizeOverworld(string root)
    {
        var source = Contained(root, Path.Combine(root, "dimensions", "minecraft", "overworld"));
        if (!Directory.Exists(source)) return;
        MoveStorageChildren(root, source, "Namespaced overworld");
    }

    private static void NormalizeWholeDimension(string root, string sourceRelative, string targetRelative, string label)
    {
        var source = Contained(root, Path.Combine(root, sourceRelative.Replace('/', Path.DirectorySeparatorChar)));
        if (!Directory.Exists(source)) return;
        var target = Contained(root, Path.Combine(root, targetRelative));
        if (Directory.Exists(target) || File.Exists(target))
            throw new InputValidationException(
                $"Namespaced {label} conflicts with existing '{targetRelative}' storage. Normalize one source before ingestion.");
        Directory.Move(source, target);
    }

    private static string Contained(string root, string candidate)
    {
        var full = Path.GetFullPath(candidate);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InputSecurityException("Dimension normalization path escapes the extracted world root.");
        return full;
    }
}
