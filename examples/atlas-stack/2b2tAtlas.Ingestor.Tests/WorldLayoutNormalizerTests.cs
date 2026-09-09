using Atlas.Ingestor.Minecraft;

namespace Atlas.Ingestor.Tests;

public sealed class WorldLayoutNormalizerTests
{
    [Theory]
    [InlineData("overworld", "region")]
    [InlineData("nether", "DIM-1/region")]
    [InlineData("end", "DIM1/region")]
    public void Explicit_dimension_normalizes_one_legacy_custom_dimension(string requested, string expected)
    {
        using var temporary = new TempDirectory();
        var source = Path.Combine(temporary.Path, "dimensions", "thearchive", "museum_2017", "region");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "r.0.0.mca"), new byte[8192]);

        WorldLayoutNormalizer.NormalizeCanonicalNamespacedDimensions(temporary.Path, [requested]);

        Assert.True(File.Exists(Path.Combine(temporary.Path, expected.Replace('/', Path.DirectorySeparatorChar), "r.0.0.mca")));
    }

    [Fact]
    public void Auto_detect_never_guesses_a_custom_dimension()
    {
        using var temporary = new TempDirectory();
        var source = Path.Combine(temporary.Path, "dimensions", "thearchive", "museum_2017", "region");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "r.0.0.mca"), new byte[8192]);

        WorldLayoutNormalizer.NormalizeCanonicalNamespacedDimensions(temporary.Path);

        Assert.True(File.Exists(Path.Combine(source, "r.0.0.mca")));
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "DIM-1")));
        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "DIM1")));
    }

    [Fact]
    public void Explicit_dimension_fails_closed_when_multiple_custom_sources_exist()
    {
        using var temporary = new TempDirectory();
        foreach (var name in new[] { "first", "second" })
        {
            var region = Path.Combine(temporary.Path, "dimensions", "thearchive", name, "region");
            Directory.CreateDirectory(region);
            File.WriteAllBytes(Path.Combine(region, "r.0.0.mca"), new byte[8192]);
        }

        var exception = Assert.Throws<Atlas.Ingestor.Security.InputValidationException>(() =>
            WorldLayoutNormalizer.NormalizeCanonicalNamespacedDimensions(temporary.Path, ["overworld"]));

        Assert.Contains("multiple custom dimensions", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
