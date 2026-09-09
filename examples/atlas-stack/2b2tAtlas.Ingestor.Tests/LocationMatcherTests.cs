using Atlas;

namespace Atlas.Ingestor.Tests;

public sealed class LocationMatcherTests
{
    private static readonly LocationMatchCandidate[] Sample =
    [
        new(1, "Spawn Base", 100, 100, 0),
        new(2, "Far Overworld Base", 40_000, 40_000, 0),
        new(3, "End City", 0, 0, 2),
    ];

    [Fact]
    public void Very_close_same_dimension_auto_attaches()
    {
        var result = LocationMatcher.Match(0, 200, 150, "Some Render", Sample);

        Assert.True(result.IsConfident);
        Assert.Equal(1, result.AutoAttachLocationId);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void Different_dimension_is_excluded()
    {
        // Coordinates coincide with the End City, but the render is Overworld.
        var result = LocationMatcher.Match(0, 0, 0, "End City", [new(3, "End City", 0, 0, 2)]);

        Assert.False(result.IsConfident);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void Medium_distance_with_strong_name_auto_attaches()
    {
        var candidates = new[] { new LocationMatchCandidate(5, "Chunk Haven", 1_500, 0, 0) };
        var result = LocationMatcher.Match(0, 0, 0, "Chunk Haven", candidates);

        Assert.True(result.IsConfident);
        Assert.Equal(5, result.AutoAttachLocationId);
    }

    [Fact]
    public void Two_close_candidates_are_ambiguous_and_park_with_suggestions()
    {
        var candidates = new[]
        {
            new LocationMatchCandidate(10, "Base A", 300, 0, 0),
            new LocationMatchCandidate(11, "Base B", 700, 0, 0),
        };
        var result = LocationMatcher.Match(0, 0, 0, "Unknown Render", candidates);

        Assert.False(result.IsConfident);
        Assert.Equal(2, result.Suggestions.Count);
        Assert.Equal(10, result.Suggestions[0].LocationId);
    }

    [Fact]
    public void Nothing_within_radius_yields_no_suggestions()
    {
        // (20000, 20000) is well beyond the 5,000-block radius of every sample candidate.
        var result = LocationMatcher.Match(0, 20_000, 20_000, "Lonely Render", Sample);

        Assert.False(result.IsConfident);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void Centroid_is_the_midpoint_of_half_open_bounds()
    {
        var (x, z) = LocationMatcher.Centroid(-192, -3248, 7488, 208);

        Assert.Equal(3648, x);
        Assert.Equal(-1520, z);
    }

    [Fact]
    public void Recognized_archive_exact_warp_and_consistent_coordinates_auto_attach()
    {
        var evidence = new ArchiveWdlEvidence
        {
            IsArchiveSource = true,
            DownloadName = "Temple_of_the_Talion_2017-03-06",
            NameCandidates = ["Temple_of_the_Talion_2017-03-06"],
        };
        var candidates = new[]
        {
            new LocationMatchCandidate(42, "Temple of the Talion", -169_902, 311_836, 0,
                ["Temple_of_the_Talion_2017-03-06"]),
        };

        var result = LocationMatcher.Match(0, -170_000, 312_000, "Archive import", candidates, archiveEvidence: evidence);

        Assert.Equal(42, result.AutoAttachLocationId);
        Assert.Contains("exact Archive warp alias", result.AutoAttachReason);
    }

    [Fact]
    public void Exact_trusted_warp_wins_when_capture_coordinates_changed()
    {
        var evidence = new ArchiveWdlEvidence
        {
            IsArchiveSource = true,
            NameCandidates = ["Temple_of_the_Talion_2017-03-06"],
        };
        var candidates = new[]
        {
            new LocationMatchCandidate(42, "Temple of the Talion", -169_902, 311_836, 0,
                ["Temple_of_the_Talion_2017-03-06"]),
        };

        var result = LocationMatcher.Match(0, 0, 0, "Archive import", candidates, archiveEvidence: evidence);

        Assert.True(result.IsConfident);
        Assert.Equal(42, result.AutoAttachLocationId);
        Assert.Contains("exact Archive warp alias", result.AutoAttachReason);
    }

    [Fact]
    public void Misowned_exact_warp_does_not_merge_a_numbered_sequel()
    {
        var warp = new ArchiveWarpCandidate("Bedrock_City_2023-06-15", "archive-download-report", 0.99, true);
        var candidates = new[]
        {
            new LocationMatchCandidate(364, "Bedrock City 2", -3_827_249, -3_438_803, 0,
                ["Bedrock_City_2023-06-15", "Bedrock_City_2_2024-05-31"]),
        };

        var result = LocationMatcher.Match(0, -3_827_249, -3_438_803, "Bedrock City", candidates,
            archiveWarp: warp);

        Assert.True(result.CreateNewLocation);
        Assert.Null(result.AutoAttachLocationId);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void Recurring_pit_fight_number_cannot_merge_despite_compound_archive_spelling()
    {
        var warp = new ArchiveWarpCandidate("PitFight_15_2023-03-07", "archive-download-report", 0.99, true);
        var candidates = new[]
        {
            new LocationMatchCandidate(1014, "Pit Fight 10", -45_570, 56_154, 0,
                ["PitFight_10_2022-02-18", "PitFight_15_2023-03-07"]),
        };

        var result = LocationMatcher.Match(0, -45_570, 56_154, "Pit Fight 15", candidates,
            archiveWarp: warp);

        Assert.True(result.CreateNewLocation);
        Assert.Null(result.AutoAttachLocationId);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void Exact_warp_from_unverified_source_requires_manual_review()
    {
        var evidence = new ArchiveWdlEvidence { NameCandidates = ["Known_Warp"] };
        var candidates = new[] { new LocationMatchCandidate(7, "Known", 1_000, 0, 0, ["Known_Warp"]) };

        var result = LocationMatcher.Match(0, 0, 0, "Unrelated", candidates, archiveEvidence: evidence);

        Assert.False(result.IsConfident);
        Assert.Equal(7, Assert.Single(result.Suggestions).LocationId);
    }

    [Fact]
    public void Different_dated_warp_of_same_location_can_match_without_coordinate_agreement()
    {
        var warp = new ArchiveWarpCandidate("Temple_of_the_Talion_2020-04-01", "archive-download-report", 0.99, true);
        var candidates = new[]
        {
            new LocationMatchCandidate(42, "Temple of the Talion", -169_902, 311_836, 0,
                ["Temple_of_the_Talion_2017-03-06"]),
        };

        var result = LocationMatcher.Match(0, 0, 0, "Later capture", candidates, archiveWarp: warp);

        Assert.True(result.IsConfident);
        Assert.Equal(42, result.AutoAttachLocationId);
        Assert.Contains("dated-warp identity", result.AutoAttachReason);
    }

    [Fact]
    public void Explicit_first_iteration_matches_unsuffixed_location_without_coordinate_agreement()
    {
        var warp = new ArchiveWarpCandidate("Helheim_1_2022-06-26", "archive-download-report", 0.99, true);
        var candidates = new[]
        {
            new LocationMatchCandidate(188, "Helheim", -1_054_833, 1_117_039, 0),
            new LocationMatchCandidate(187, "Helheim 2", -350_906, -281_982, 0),
        };

        var result = LocationMatcher.Match(0, -346_964, -262_994, "Helheim 1", candidates,
            archiveWarp: warp);

        Assert.True(result.IsConfident);
        Assert.Equal(188, result.AutoAttachLocationId);
        Assert.Contains("dated-warp identity", result.AutoAttachReason);
    }

    [Fact]
    public void Uncorroborated_prior_render_containment_cannot_identify_a_distant_location()
    {
        var candidates = new[]
        {
            new LocationMatchCandidate(12, "Historic Base", 50_000, 50_000, 0, [],
                [new LocationRenderFootprint(10_000, 10_000, 20_000, 20_000)]),
        };
        var result = LocationMatcher.Match(0, 15_000, 15_000, "Renamed capture", candidates,
            incomingFootprint: new LocationRenderFootprint(10_500, 10_500, 19_500, 19_500));

        Assert.False(result.IsConfident);
        Assert.Null(result.AutoAttachLocationId);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void Mu_megabase_containment_does_not_outrank_DonFuer_identity()
    {
        var warp = new ArchiveWarpCandidate(
            "Trost_Gate,_DonFuer_2022-12-26", "archive-download-report", 0.99, true);
        var incomingFootprint = new LocationRenderFootprint(653_616, 416_528, 654_448, 417_520);
        var candidates = new[]
        {
            // Mu's original multi-million-block WDL contains the entire incoming capture even
            // though its Atlas location and Archive identity are unrelated and very far away.
            new LocationMatchCandidate(5, "Mu Megabase", 5_168_556, 10_320_373, 0,
                ["Mu_2020-07-23"],
                [new LocationRenderFootprint(-192, -192, 5_171_232, 10_322_528)]),
            new LocationMatchCandidate(600, "DonFuer", 655_405, 418_281, 0,
                ["DonFuer_20_2021-05-15"]),
        };

        var result = LocationMatcher.Match(0, 654_032, 416_856, "DonFuer Trost Gate 2022 12 26", candidates,
            archiveWarp: warp, incomingFootprint: incomingFootprint);

        Assert.Equal(600, result.AutoAttachLocationId);
        Assert.DoesNotContain(result.Suggestions, suggestion => suggestion.LocationId == 5);
        Assert.DoesNotContain("prior-render overlap", result.AutoAttachReason);
    }

    [Fact]
    public void Spawnmason_sky_subbuilds_do_not_merge_into_a_different_collection_leaf()
    {
        var incoming = new ArchiveWarpCandidate(
            "Sky:_Bird_Asian_District_2022-10-03@Spawnmasons", "archive-download-report", 0.99, true);
        var sharedFootprint = new LocationRenderFootprint(825_520, 437_664, 828_128, 440_208);
        var candidates = new[]
        {
            new LocationMatchCandidate(1254, "Sky Masons", 827_065, 439_090, 0,
                ["Sky:_Hotel_Ukraina_2022-10-03@Spawnmasons"], [sharedFootprint]),
        };

        var result = LocationMatcher.Match(0, 826_965, 438_873, "Bird Asian District", candidates,
            archiveWarp: incoming, incomingFootprint: sharedFootprint);

        Assert.Null(result.AutoAttachLocationId);
        Assert.True(result.CreateNewLocation);
        Assert.DoesNotContain(result.Suggestions, suggestion => suggestion.LocationId == 1254);
    }

    [Fact]
    public void Trusted_different_exhibit_without_collection_does_not_merge_on_proximity_alone()
    {
        var incoming = new ArchiveWarpCandidate(
            "l21w47_KOTH_island_2021-11-20@Spawnmason_lodge", "archive-download-report", 0.99, true);
        var candidates = new[]
        {
            new LocationMatchCandidate(1254, "Hotel Ukraina", 826_666, 438_819, 0,
                ["Sky:_Hotel_Ukraina_2022-10-03@Spawnmasons"]),
        };

        var result = LocationMatcher.Match(0, 827_227, 439_345, "KOTH Island", candidates,
            archiveWarp: incoming);

        Assert.False(result.IsConfident);
        Assert.Equal(1254, Assert.Single(result.Suggestions).LocationId);
    }

    [Fact]
    public void Trusted_unique_archive_warp_with_no_candidate_is_confident_new()
    {
        var warp = new ArchiveWarpCandidate("Never_Seen_Base_2021-05-01", "archive-download-report", 0.99, true);
        var result = LocationMatcher.Match(0, 900_000, -800_000, "Never Seen Base", [], archiveWarp: warp);

        Assert.True(result.IsConfident);
        Assert.True(result.CreateNewLocation);
        Assert.Null(result.AutoAttachLocationId);
    }

    [Fact]
    public void Trusted_warp_near_an_uncertain_existing_location_requires_review_instead_of_creating_duplicate()
    {
        var warp = new ArchiveWarpCandidate("Unlabeled_Restoration_2021-05-01", "archive-download-report", 0.99, true);
        var candidates = new[]
        {
            new LocationMatchCandidate(19, "Possible Historical Build", 1_500, 0, 0),
        };

        var result = LocationMatcher.Match(0, 0, 0, "Different Museum Name", candidates, archiveWarp: warp);

        Assert.False(result.IsConfident);
        Assert.False(result.CreateNewLocation);
        Assert.Equal(19, Assert.Single(result.Suggestions).LocationId);
    }
}
