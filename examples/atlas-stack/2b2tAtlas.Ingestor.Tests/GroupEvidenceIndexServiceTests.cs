using System.Text.Json;
using Atlas;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using _2b2tAtlas.Server.Services.AiEnrichment;
using ServerGroup = _2b2tAtlas.Server.Models.Group;

namespace Atlas.Ingestor.Tests;

public sealed class GroupEvidenceIndexServiceTests
{
    [Fact]
    public void Infobox_base_evidence_is_revision_pinned_and_auto_apply_eligible()
    {
        using var fixture = new Fixture();
        fixture.WriteIndex(new
        {
            schemaVersion = 1,
            generatedUtc = DateTime.UtcNow,
            articles = new[]
            {
                Article(12, "DonFuer", "https://2b2t.wikioasis.org/wiki/DonFuer", 9981,
                    Match(220, "Infobox group bases")),
            },
        });

        var suggestions = fixture.Service.FindMatches(
            new Location { Rowid = 220, Name = "DonFuer 20", Groups = [] },
            [Group(12, "DonFuer")]);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(12, suggestion.GroupId);
        Assert.Equal("DonFuer", suggestion.GroupName);
        Assert.Equal(9981, suggestion.SourceRevisionId);
        Assert.Equal(0.97, suggestion.Confidence);
        Assert.True(suggestion.AutoApplyEligible);
        Assert.Contains("Infobox", suggestion.Evidence);
    }

    [Fact]
    public void Base_section_evidence_is_suggested_but_requires_review()
    {
        using var fixture = new Fixture();
        fixture.WriteIndex(new
        {
            schemaVersion = 1,
            generatedUtc = DateTime.UtcNow,
            articles = new[] { Article(1, "SpawnMasons", "https://example.test/SpawnMasons", 44,
                Match(55, "base/build section")) },
        });

        var suggestion = Assert.Single(fixture.Service.FindMatches(
            new Location { Rowid = 55, Name = "A Lodge", Groups = [] }, [Group(1, "SpawnMasons")]));

        Assert.Equal(0.84, suggestion.Confidence);
        Assert.False(suggestion.AutoApplyEligible);
    }

    [Fact]
    public void Group_title_location_identity_requires_review()
    {
        using var fixture = new Fixture();
        fixture.WriteIndex(new
        {
            schemaVersion = 1,
            generatedUtc = DateTime.UtcNow,
            articles = new[] { Article(42, "Wingston", "https://example.test/Wingston", 79718,
                Match(1181, "building-group title equals location")) },
        });

        var suggestion = Assert.Single(fixture.Service.FindMatches(
            new Location { Rowid = 1181, Name = "Wingston", Groups = [] }, [Group(42, "Wingston")]));

        Assert.Equal(0.88, suggestion.Confidence);
        Assert.False(suggestion.AutoApplyEligible);
    }

    [Fact]
    public void Incidental_links_and_existing_attributions_are_ignored()
    {
        using var fixture = new Fixture();
        fixture.WriteIndex(new
        {
            schemaVersion = 1,
            generatedUtc = DateTime.UtcNow,
            articles = new[]
            {
                new
                {
                    publicUrl = "https://example.test/Astral",
                    revisionId = 2,
                    atlasGroups = new[] { new { id = 4, name = "Astral Brotherhood" } },
                    exactAtlasBuildMatches = Array.Empty<object>(),
                    allLinkedAtlasLocations = new[] { new { Rowid = 80, Name = "Rat House" } },
                },
                Article(4, "Astral Brotherhood", "https://example.test/Astral", 2,
                    Match(81, "Infobox group bases")),
            },
        });

        Assert.Empty(fixture.Service.FindMatches(
            new Location { Rowid = 80, Name = "Rat House", Groups = [] }, [Group(4, "Astral Brotherhood")]));
        Assert.Empty(fixture.Service.FindMatches(
            new Location
            {
                Rowid = 81,
                Name = "Menegroth",
                Groups = [new LocationGroupAttribution { GroupId = 4, GroupName = "Astral Brotherhood" }],
            },
            [Group(4, "Astral Brotherhood")]));
    }

    [Fact]
    public void Existing_attribution_can_be_returned_as_revision_pinned_drafting_context()
    {
        using var fixture = new Fixture();
        fixture.WriteIndex(new
        {
            schemaVersion = 1,
            generatedUtc = DateTime.UtcNow,
            articles = new[] { Article(4, "Astral Brotherhood", "https://example.test/Astral", 902,
                Match(81, "Infobox group bases")) },
        });

        var suggestion = Assert.Single(fixture.Service.FindMatches(
            new Location
            {
                Rowid = 81,
                Name = "Menegroth",
                Groups = [new LocationGroupAttribution { GroupId = 4, GroupName = "Astral Brotherhood" }],
            },
            [Group(4, "Astral Brotherhood")],
            includeExisting: true));

        Assert.Equal(4, suggestion.GroupId);
        Assert.Equal(902, suggestion.SourceRevisionId);
        Assert.True(suggestion.AutoApplyEligible);
    }

    [Fact]
    public void Ambiguous_group_identity_fails_closed()
    {
        using var fixture = new Fixture();
        fixture.WriteIndex(new
        {
            schemaVersion = 1,
            generatedUtc = DateTime.UtcNow,
            articles = new[]
            {
                new
                {
                    publicUrl = "https://example.test/Imperium",
                    revisionId = 7,
                    atlasGroups = new[]
                    {
                        new { id = 20, name = "The Imperials" },
                        new { id = 21, name = "The Emperium" },
                    },
                    exactAtlasBuildMatches = new[] { Match(90, "Infobox group bases") },
                },
            },
        });

        var suggestions = fixture.Service.FindMatches(
            new Location { Rowid = 90, Name = "Empire Base", Groups = [] },
            [Group(20, "The Imperials"), Group(21, "The Emperium")]);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void Roman_arabic_equivalence_keeps_iteration_and_reduces_confidence()
    {
        using var fixture = new Fixture();
        fixture.WriteIndex(new
        {
            schemaVersion = 1,
            generatedUtc = DateTime.UtcNow,
            articles = new[] { Article(3, "Invictus", "https://example.test/Invictus", 18,
                Match(71, "Infobox group bases", "trailing Roman/Arabic iteration equivalence")) },
        });

        var suggestion = Assert.Single(fixture.Service.FindMatches(
            new Location { Rowid = 71, Name = "Invictus 1", Groups = [] }, [Group(3, "Invictus")]));

        Assert.Equal(0.94, suggestion.Confidence, 2);
        Assert.True(suggestion.AutoApplyEligible);
        Assert.Empty(fixture.Service.FindMatches(
            new Location { Rowid = 72, Name = "Invictus", Groups = [] }, [Group(3, "Invictus")]));
    }

    [Fact]
    public void Stale_index_is_reported_and_not_used()
    {
        using var fixture = new Fixture(maxAgeDays: 2);
        fixture.WriteIndex(new
        {
            schemaVersion = 1,
            generatedUtc = DateTime.UtcNow.AddDays(-3),
            articles = new[] { Article(1, "Example", "https://example.test", 1,
                Match(1, "Infobox group bases")) },
        });

        Assert.Empty(fixture.Service.FindMatches(
            new Location { Rowid = 1, Name = "Example", Groups = [] }, [Group(1, "Example")]));
        var status = fixture.Service.GetStatus();
        Assert.False(status.Available);
        Assert.Contains("stale", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Discovery_requires_an_explicit_build_and_honors_the_deny_list()
    {
        using var fixture = new Fixture();
        fixture.WriteIndex(new
        {
            schemaVersion = 1,
            generatedUtc = DateTime.UtcNow,
            articles = new object[]
            {
                new
                {
                    pageId = 100,
                    title = "The Republic",
                    name = "The Republic",
                    type = "Other",
                    publicUrl = "https://2b2t.wikioasis.org/wiki/The_Republic",
                    revisionId = 555,
                    intro = "A group with a documented base.",
                    atlasGroups = Array.Empty<object>(),
                    exactAtlasBuildMatches = new[] { new
                    {
                        Rowid = 700,
                        Name = "Republic Base",
                        evidence = new[] { "Infobox group bases" },
                    } },
                },
                new
                {
                    pageId = 101,
                    title = "Omega City",
                    name = "Omega City",
                    type = "Build",
                    publicUrl = "https://2b2t.wikioasis.org/wiki/Omega_City",
                    revisionId = 556,
                    intro = "A base project incorrectly categorized as a group.",
                    atlasGroups = Array.Empty<object>(),
                    exactAtlasBuildMatches = new[] { new
                    {
                        Rowid = 701,
                        Name = "Omega City",
                        evidence = new[] { "building-group title equals location" },
                    } },
                },
            },
        });

        var candidate = Assert.Single(fixture.Service.GetGroupDiscoveryCandidates([]));
        Assert.Equal("The Republic", candidate.Name);
        Assert.Equal(555, candidate.SourceRevisionId);
        Assert.Equal("Republic Base", Assert.Single(candidate.Locations).LocationName);
    }

    private static object Article(int groupId, string groupName, string url, long revisionId, params object[] matches) => new
    {
        publicUrl = url,
        revisionId,
        atlasGroups = new[] { new { id = groupId, name = groupName } },
        exactAtlasBuildMatches = matches,
    };

    private static object Match(int rowid, params string[] evidence) => new { Rowid = rowid, evidence };

    private static ServerGroup Group(int id, string name) => new() { Id = id, Name = name };

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "atlas-group-evidence-" + Guid.NewGuid().ToString("N"));
        private readonly string _path;
        private readonly AiEnrichmentOptions _options;

        public Fixture(int maxAgeDays = 14)
        {
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, "index.json");
            _options = new AiEnrichmentOptions
            {
                GroupEvidenceIndexPath = _path,
                GroupEvidenceMaxAgeDays = maxAgeDays,
            };
            Service = new GroupEvidenceIndexService(
                Options.Create(_options),
                new TestEnvironment { ContentRootPath = _directory },
                NullLogger<GroupEvidenceIndexService>.Instance);
        }

        public GroupEvidenceIndexService Service { get; }

        public void WriteIndex(object value) => File.WriteAllText(_path,
            JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Atlas.Tests";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
