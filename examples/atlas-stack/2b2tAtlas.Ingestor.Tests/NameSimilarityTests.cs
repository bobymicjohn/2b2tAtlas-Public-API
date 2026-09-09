using _2b2tAtlas.Server.Services.AiEnrichment;

namespace Atlas.Ingestor.Tests;

public sealed class NameSimilarityTests
{
    [Fact]
    public void Identical_names_score_one()
    {
        Assert.Equal(1.0, NameSimilarity.Score("Spawn Base", "Spawn Base"));
    }

    [Fact]
    public void Normalization_ignores_case_and_punctuation()
    {
        Assert.Equal(1.0, NameSimilarity.Score("The-End: City!", "the end city"));
    }

    [Fact]
    public void Prefix_scores_high()
    {
        Assert.True(NameSimilarity.Score("Boby", "Boby's Bottom") >= 0.8);
    }

    [Fact]
    public void Unrelated_names_score_zero()
    {
        Assert.Equal(0, NameSimilarity.Score("Spawn Base", "Frostbyte Citadel"));
    }

    [Fact]
    public void Empty_input_scores_zero()
    {
        Assert.Equal(0, NameSimilarity.Score("", "Anything"));
        Assert.Equal(0, NameSimilarity.Score("Anything", null));
    }
}
