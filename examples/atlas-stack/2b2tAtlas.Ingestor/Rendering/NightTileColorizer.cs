using Atlas;
using Atlas.Ingestor.Security;
using SkiaSharp;

namespace Atlas.Ingestor.Rendering;

/// <summary>Restores terrain chroma to uNmINeD night tiles without replacing its light map.</summary>
public static class NightTileColorizer
{
    /// <summary>Atomically grades every paired native maximum-detail PNG.</summary>
    public static async Task ApplyAsync(
        string dayRoot,
        string nightRoot,
        NightColorGradeOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled) return;
        if (!Directory.Exists(dayRoot) || !Directory.Exists(nightRoot))
            throw new InputValidationException("Paired day/night native tile roots are required for night grading.");

        var dayFiles = Directory.EnumerateFiles(dayRoot, "*.png", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(dayRoot, path), StringComparer.OrdinalIgnoreCase);
        var nightFiles = Directory.EnumerateFiles(nightRoot, "*.png", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(nightRoot, path), StringComparer.OrdinalIgnoreCase);
        if (dayFiles.Count == 0 || nightFiles.Count == 0)
            throw new InputValidationException("Paired day/night native tile inventories are empty.");
        if (dayFiles.Count != nightFiles.Count || dayFiles.Keys.Any(relative => !nightFiles.ContainsKey(relative)))
            throw new InputSecurityException("Day and night native tile inventories differ; night grading was refused.");

        await Parallel.ForEachAsync(dayFiles, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 12),
        }, (entry, token) =>
        {
            token.ThrowIfCancellationRequested();
            GradeTile(entry.Value, nightFiles[entry.Key], options);
            return ValueTask.CompletedTask;
        });
    }

    private static void GradeTile(string dayPath, string nightPath, NightColorGradeOptions options)
    {
        using var day = SKBitmap.Decode(dayPath)
            ?? throw new InputValidationException($"Day tile is not a valid PNG: {dayPath}");
        using var night = SKBitmap.Decode(nightPath)
            ?? throw new InputValidationException($"Night tile is not a valid PNG: {nightPath}");
        if (day.Width != night.Width || day.Height != night.Height)
            throw new InputSecurityException($"Paired day/night tile dimensions differ: {nightPath}");

        var dayPixels = day.Pixels;
        var nightPixels = night.Pixels;
        var outputPixels = new SKColor[nightPixels.Length];
        for (var index = 0; index < nightPixels.Length; index++)
        {
            var d = dayPixels[index];
            var n = nightPixels[index];
            if (n.Alpha == 0)
            {
                outputPixels[index] = n;
                continue;
            }
            var graded = NightColorGrade.Apply(
                d.Red, d.Green, d.Blue, n.Red, n.Green, n.Blue,
                options.Saturation, options.Lightness);
            outputPixels[index] = new SKColor(graded.R, graded.G, graded.B, n.Alpha);
        }

        using var output = new SKBitmap(new SKImageInfo(
            night.Width, night.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        output.Pixels = outputPixels;
        using var image = SKImage.FromBitmap(output);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InputValidationException($"Night tile could not be encoded: {nightPath}");
        var temporary = nightPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using (var stream = File.Create(temporary)) encoded.SaveTo(stream);
            File.Move(temporary, nightPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
