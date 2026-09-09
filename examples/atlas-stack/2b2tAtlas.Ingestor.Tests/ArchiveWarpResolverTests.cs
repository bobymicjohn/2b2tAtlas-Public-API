using Atlas;

namespace Atlas.Ingestor.Tests;

public sealed class ArchiveWarpResolverTests
{
    [Fact]
    public void Recognized_download_report_is_the_authoritative_warp()
    {
        var evidence = new ArchiveWdlEvidence
        {
            IsArchiveSource = true,
            DownloadName = "Temple_of_the_Talion_2017-03-06",
        };

        var result = ArchiveWarpResolver.Resolve(evidence, "wrong-file.zip", "other");

        Assert.NotNull(result);
        Assert.Equal("Temple_of_the_Talion_2017-03-06", result.Name);
        Assert.Equal("archive-download-report", result.Source);
        Assert.True(result.IsTrusted);
    }

    [Fact]
    public void Archive_attributed_worldtools_filename_strips_capture_epoch_only()
    {
        var evidence = new ArchiveWdlEvidence { IsArchiveSource = true, DownloaderKind = "worldtools" };

        var result = ArchiveWarpResolver.Resolve(
            evidence, "Temple_of_the_Talion_2017-03-06_1723456789000.zip", "The Archive");

        Assert.Equal("Temple_of_the_Talion_2017-03-06", result?.Name);
    }

    [Fact]
    public void Generic_filename_without_archive_attribution_is_not_a_warp()
    {
        Assert.Null(ArchiveWarpResolver.Resolve(null, "my-base.zip", "2b2t world download"));
    }

    [Theory]
    [InlineData("Summermelon_finished@concepts", true)]
    [InlineData("Smibville_2020-01-12_(concept)@Experiments", true)]
    [InlineData("Space_Valkyria_III_complete_(concept_build)@concepts", true)]
    [InlineData("Space_Valkyria_III_first_site_2017-01-01@End", false)]
    [InlineData("Conceptual_City_2020-01-01", false)]
    public void Singleplayer_concepts_require_an_explicit_concept_token(string value, bool expected)
    {
        Assert.Equal(expected, ArchiveWarpResolver.IsSinglePlayerConcept(value));
    }

    [Theory]
    [InlineData("Temple_of_the_Talion_2017-03-06", "Temple of the Talion")]
    [InlineData("Temple_of_the_Talion_2020-12", "Temple of the Talion")]
    [InlineData("l22w07_Loo_Lodge_4_2022-02-12@SpawnMason_Lodge", "loo lodge 4")]
    [InlineData("PitFight_11_2022-03-17", "pit fight 11")]
    public void Identity_removes_capture_date_but_preserves_location(string warp, string expected)
    {
        Assert.Equal(expected, ArchiveWarpResolver.Identity(warp), ignoreCase: true);
    }

    [Theory]
    [InlineData("Bedrock_City_2023-06-15", "Bedrock City 2", true)]
    [InlineData("Bedrock_City_II_2023-06-15", "Bedrock City 2", false)]
    [InlineData("Bedrock_City_2_2023-06-15", "Bedrock City", true)]
    [InlineData("Helheim_1_2022-06-26", "Helheim", false)]
    [InlineData("Fusionia_I_2025-03-28", "Fusionia", false)]
    [InlineData("Helheim_II_2022-06-15", "Helheim", true)]
    [InlineData("Geezer_Town_XI_2024-07-12", "Geezer Town 1", true)]
    [InlineData("Geezer_Town_XI_2024-07-12", "Geezer Town 11", false)]
    [InlineData("Geezer_Town_XIX_2025-06-04", "Geezer Town 19", false)]
    [InlineData("Temple_of_the_Talion_2017-03-06", "Temple of the Talion", false)]
    [InlineData("PitFight_11_2022-03-17", "Pit Fight 10", true)]
    [InlineData("PitFight_11_2022-03-17", "Pit Fight 11", false)]
    public void Numbered_iterations_are_identity_bearing(string warp, string location, bool expected)
    {
        Assert.Equal(expected, ArchiveWarpResolver.HasIterationConflict(warp, location));
    }

    [Theory]
    [InlineData("Helheim_1_2022-06-26", "helheim")]
    [InlineData("Fusionia_I_2025-03-28", "fusionia")]
    [InlineData("Helheim_II_2022-06-15", "helheim 2")]
    [InlineData("Geezer_Town_XI_2024-07-12", "geezer town 11")]
    [InlineData("Geezer_Town_XVI_2025-01-25", "geezer town 16")]
    [InlineData("Geezer_Town_XIX_2025-06-04", "geezer town 19")]
    public void Canonical_location_identity_folds_only_the_first_iteration(string value, string expected)
    {
        Assert.Equal(expected, ArchiveWarpResolver.CanonicalLocationIdentity(value));
    }

    [Theory]
    [InlineData("Bedrock_City_2023-06-15", "Bedrock City")]
    [InlineData("Wässrige_Hölle_Zwei_2024-02-01", "Wässrige Hölle Zwei")]
    [InlineData("Bedrock_City_2_2024-05-31", "Bedrock City 2")]
    [InlineData("Coliseum,_The_2022-05-04", "The Coliseum")]
    [InlineData("Nether_Highway_Inn_2023-08-25@Nether", "Nether Highway Inn")]
    [InlineData("Sky:_Hotel_Ukraina_2022-10-03@Spawnmasons", "Hotel Ukraina")]
    [InlineData("l21w47_KOTH_island_2021-11-20@Spawnmason_lodge", "KOTH island")]
    [InlineData("PitFight_15_2023-03-07", "Pit Fight 15")]
    public void Display_identity_preserves_human_name_and_removes_capture_date(string warp, string expected)
    {
        Assert.Equal(expected, ArchiveWarpResolver.DisplayIdentity(warp));
    }

    [Theory]
    [InlineData("x-1.0m_2015-08-29", "-X 1M Milestone")]
    [InlineData("x6.0m_2020-01-01", "+X 6M Milestone")]
    [InlineData("z-0.55m_nether_2020-01-01", "Nether -Z 550k Milestone")]
    [InlineData("x3.57m_Nether_2017-04-09_SCC_Monument", "SCC Monument (Nether +X 3.57M)")]
    [InlineData("0.400m,_0.400m_2020-01-01", "+X 400k / +Z 400k Milestone")]
    [InlineData("-1.0m,_1.0m_2020-01-01", "-X 1M / +Z 1M Milestone")]
    [InlineData("x0.500m_Semisphere_at_500k_2020-01-01", "Semisphere at 500k (+X 500k)")]
    public void Coordinate_warps_get_sign_aware_human_names(string warp, string expected)
    {
        Assert.Equal(expected, ArchiveWarpResolver.DisplayIdentity(warp));
    }

    [Fact]
    public void Coordinate_identity_ignores_date_but_preserves_direction()
    {
        var earlier = ArchiveWarpResolver.CanonicalLocationIdentity("x-1.0m_2015-08-29");
        var later = ArchiveWarpResolver.CanonicalLocationIdentity("x-1.0m_2018-03-02");
        var opposite = ArchiveWarpResolver.CanonicalLocationIdentity("x1.0m_2018-03-02");

        Assert.Equal(earlier, later);
        Assert.NotEqual(earlier, opposite);
    }

    [Fact]
    public void Spawnmason_sky_collection_preserves_each_leaf_as_its_location_identity()
    {
        Assert.Equal("sky", ArchiveWarpResolver.ExplicitExhibitCollection(
            "Sky:_Bird_Asian_District_2022-10-03@Spawnmasons"));
        Assert.Equal("bird asian district", ArchiveWarpResolver.CanonicalLocationIdentity(
            "Sky:_Bird_Asian_District_2022-10-03@Spawnmasons"));
        Assert.Equal("hotel ukraina", ArchiveWarpResolver.CanonicalLocationIdentity(
            "Sky:_Hotel_Ukraina_2022-10-03@Spawnmasons"));
        Assert.NotEqual(
            ArchiveWarpResolver.CanonicalLocationIdentity("Sky:_Bird_Asian_District_2022-10-03@Spawnmasons"),
            ArchiveWarpResolver.CanonicalLocationIdentity("Sky:_Hotel_Ukraina_2022-10-03@Spawnmasons"));
    }

    [Theory]
    [InlineData("Sky:_Hotel_Ukraina_2022-10-03@Spawnmasons", 254)]
    [InlineData("Sky:_Ocean_Island_2022-10-03@Spawnmasons", 254)]
    [InlineData("Sky:_Ceiling_2022-10-03@Spawnmasons", null)]
    [InlineData("Hotel_Ukraina_2022-10-03", null)]
    [InlineData("l22w07_Loo_Lodge_4_2022-02-12@SpawnMason_Lodge", null)]
    public void Sky_exhibits_use_the_historical_build_limit_cutaway_except_the_ceiling(
        string warp,
        int? expected)
    {
        Assert.Equal(expected, ArchiveWarpResolver.RecommendedRenderTopY(
            new ArchiveWarpCandidate(warp, "archive-download-report", 1, true)));
    }

    [Fact]
    public void Untrusted_sky_like_name_cannot_enable_a_render_cutaway()
    {
        Assert.Null(ArchiveWarpResolver.RecommendedRenderTopY(new ArchiveWarpCandidate(
            "Sky:_Hotel_Ukraina_2022-10-03@Spawnmasons", "filename", 0.5, false)));
    }
}
