using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerGroup = _2b2tAtlas.Server.Models.Group;
using ServerLocation = _2b2tAtlas.Server.Models.Location;

namespace Atlas.Ingestor.Tests;

public sealed class CanonicalContentSeederTests
{
    [Fact]
    public async Task SpawnMason_archive_suffix_links_every_owned_location()
    {
        await using var fixture = await Fixture.CreateAsync();
        var lodge = fixture.AddLocation("Loo Lodge 99");
        var unrelated = fixture.AddLocation("Unrelated");
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.Warps.AddRange(
            new Warp { Name = "l99_Loo_Lodge_99@SpawnMason_Lodge", LocationRowid = lodge.Rowid, TimeAdded = DateTime.UtcNow.ToString("o") },
            new Warp { Name = "Something@SomeoneElse", LocationRowid = unrelated.Rowid, TimeAdded = DateTime.UtcNow.ToString("o") });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new GroupSeeder(fixture.Context, NullLogger<GroupSeeder>.Instance).SeedAsync();

        var spawnMasons = await fixture.Context.Groups.SingleAsync(group => group.Name == "SpawnMasons", TestContext.Current.CancellationToken);
        Assert.Contains(await fixture.Context.LocationGroups.ToListAsync(TestContext.Current.CancellationToken),
            link => link.GroupId == spawnMasons.Id && link.LocationRowid == lodge.Rowid);
        Assert.DoesNotContain(await fixture.Context.LocationGroups.ToListAsync(TestContext.Current.CancellationToken),
            link => link.GroupId == spawnMasons.Id && link.LocationRowid == unrelated.Rowid);
    }

    [Fact]
    public async Task SpawnMasons_does_not_claim_the_Imperials_vanity_invite()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Context.Groups.Add(new ServerGroup
        {
            Name = "SpawnMasons", Type = "Build", Description = "Operator-reviewed history",
            DiscordUrl = "https://discord.gg/spawnmasons", Founded = "2017", Status = "Active",
            LogoSourceUrl = "https://example.org/source", DateAddedUtc = DateTime.UtcNow.ToString("o"),
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new GroupSeeder(fixture.Context, NullLogger<GroupSeeder>.Instance).SeedAsync();

        var spawnMasons = await fixture.Context.Groups.SingleAsync(group => group.Name == "SpawnMasons", TestContext.Current.CancellationToken);
        var imperials = await fixture.Context.Groups.SingleAsync(group => group.Name == "The Imperials", TestContext.Current.CancellationToken);
        Assert.Null(spawnMasons.DiscordUrl);
        Assert.Equal("Operator-reviewed history", spawnMasons.Description);
        Assert.Equal("https://discord.gg/spawnmasons", imperials.DiscordUrl);
    }

    [Fact]
    public async Task SouthernCanal_is_two_disconnected_overworld_segments()
    {
        await using var fixture = await Fixture.CreateAsync();
        await new GroupSeeder(fixture.Context, NullLogger<GroupSeeder>.Instance).SeedAsync();
        await new HighwaySeeder(fixture.Context, NullLogger<HighwaySeeder>.Instance).SeedAsync();

        var segments = await fixture.Context.Highways.Where(highway => highway.Name.StartsWith("Southern Canal"))
            .OrderBy(highway => highway.Name).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, segments.Count);
        Assert.All(segments, segment => Assert.Equal((int)Dimension.Overworld, segment.Dimension));
        Assert.DoesNotContain(segments, segment =>
        {
            var points = JsonSerializer.Deserialize<int[][]>(segment.PointsJson)!;
            return points.Any(point => point[1] < 2_000) && points.Any(point => point[1] > 29_000_000);
        });
        var border = Assert.Single(segments, segment => segment.Name.Contains("world-border"));
        Assert.Equal(12_000, border.LengthBlocks);
        Assert.True(border.DisplayWeight >= 7);
        Assert.Equal(3, await fixture.Context.HighwayGroups.CountAsync(link => link.HighwayId == border.Id,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Highway_history_preserves_primary_and_contributor_groups()
    {
        await using var fixture = await Fixture.CreateAsync();
        await new GroupSeeder(fixture.Context, NullLogger<GroupSeeder>.Instance).SeedAsync();
        await new HighwaySeeder(fixture.Context, NullLogger<HighwaySeeder>.Instance).SeedAsync();

        var plusX = await fixture.Context.Highways.SingleAsync(highway => highway.Name == "+X Highway",
            TestContext.Current.CancellationToken);
        var links = await fixture.Context.HighwayGroups.Where(link => link.HighwayId == plusX.Id)
            .Include(link => link.Group)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(links, link => link.Group.Name == "Highway Workers Union (HWU)" && link.Role.Contains("Primary"));
        Assert.Contains(links, link => link.Group.Name == "Motorway Extension Gurus (MEG)");
        Assert.Contains(links, link => link.Group.Name == "Independent Interstate Society (IIS)");
    }

    [Fact]
    public async Task Reviewed_owner_suffixes_and_build_families_add_group_links()
    {
        await using var fixture = await Fixture.CreateAsync();
        var emperiumBase = fixture.AddLocation("An old member base");
        var sbaBase = fixture.AddLocation("SBA 42");
        var donFuerBase = fixture.AddLocation("DonFuer 99");
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.Context.Warps.Add(new Warp
        {
            Name = "Old_member_base@Emperium",
            LocationRowid = emperiumBase.Rowid,
            TimeAdded = DateTime.UtcNow.ToString("o"),
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new GroupSeeder(fixture.Context, NullLogger<GroupSeeder>.Instance).SeedAsync();

        var links = await fixture.Context.LocationGroups.Include(link => link.Group)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(links, link => link.LocationRowid == emperiumBase.Rowid && link.Group.Name == "The Emperium");
        Assert.Contains(links, link => link.LocationRowid == sbaBase.Rowid && link.Group.Name == "Spawn Builders Association");
        Assert.Contains(links, link => link.LocationRowid == donFuerBase.Rowid && link.Group.Name == "DonFuer");
    }

    [Fact]
    public async Task Imperials_project_index_links_distinctive_exact_names_only()
    {
        await using var fixture = await Fixture.CreateAsync();
        var karthwasten = fixture.AddLocation("Karthwasten");
        var emfinity = fixture.AddLocation("Emfinity I");
        var genericOasis = fixture.AddLocation("Oasis");
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new GroupSeeder(fixture.Context, NullLogger<GroupSeeder>.Instance).SeedAsync();

        var links = await fixture.Context.LocationGroups.Include(link => link.Group)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(links, link => link.LocationRowid == karthwasten.Rowid && link.Group.Name == "The Imperials");
        Assert.Contains(links, link => link.LocationRowid == emfinity.Rowid && link.Group.Name == "The Imperials");
        Assert.DoesNotContain(links, link => link.LocationRowid == genericOasis.Rowid && link.Group.Name == "The Imperials");
    }

    [Fact]
    public async Task Wiki_audit_seeds_declared_builds_but_not_incidental_mentions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var solaris = fixture.AddLocation("Solaris");
        var ratHouse = fixture.AddLocation("Rat House");
        var bedrockCity = fixture.AddLocation("Bedrock City");
        var donFuer = fixture.AddLocation("DonFuer");
        var heimsland = fixture.AddLocation("Heimsland");
        var chunkHaven = fixture.AddLocation("Chunk Haven");
        var commiegrad = fixture.AddLocation("c0mmiegrad");
        var boedecken = fixture.AddLocation("The Boedecken");
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new GroupSeeder(fixture.Context, NullLogger<GroupSeeder>.Instance).SeedAsync();

        var links = await fixture.Context.LocationGroups.Include(link => link.Group)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(links, link => link.LocationRowid == solaris.Rowid && link.Group.Name == "Astral Brotherhood");
        Assert.DoesNotContain(links, link => link.LocationRowid == ratHouse.Rowid && link.Group.Name == "Astral Brotherhood");
        Assert.Contains(links, link => link.LocationRowid == bedrockCity.Rowid && link.Group.Name == "Bedrock City");
        Assert.DoesNotContain(links, link => link.LocationRowid == donFuer.Rowid && link.Group.Name == "Bedrock City");
        Assert.DoesNotContain(links, link => link.LocationRowid == donFuer.Rowid && link.Group.Name == "Spawn Builders Association");
        Assert.DoesNotContain(links, link => link.LocationRowid == donFuer.Rowid && link.Group.Name == "The Enclave");
        Assert.DoesNotContain(links, link => link.LocationRowid == donFuer.Rowid && link.Group.Name == "Vortex Coalition");
        Assert.Contains(links, link => link.LocationRowid == heimsland.Rowid && link.Group.Name == "Obscension");
        Assert.DoesNotContain(links, link => link.LocationRowid == chunkHaven.Rowid && link.Group.Name == "Obscension");
        Assert.Contains(links, link => link.LocationRowid == commiegrad.Rowid && link.Group.Name == "The Gulag");
        Assert.DoesNotContain(links, link => link.LocationRowid == boedecken.Rowid && link.Group.Name == "The Gulag");
    }

    [Fact]
    public async Task Canonical_wiki_links_migrate_from_Miraheze_to_WikiOasis()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Context.Groups.Add(new ServerGroup
        {
            Name = "DonFuer", Type = "Build", Description = "Operator-reviewed history",
            WikiUrl = "https://2b2t.miraheze.org/wiki/DonFuer", Founded = "2013", Status = "Active",
            LogoSourceUrl = "https://example.org/source", DateAddedUtc = DateTime.UtcNow.ToString("o"),
        });
        fixture.Context.Groups.Add(new ServerGroup
        {
            Name = "Valkyria", Type = "Other", Description = "Operator-reviewed faction history",
            WikiUrl = "https://2b2t.miraheze.org/wiki/Valkyria", Founded = "2013", Status = "Disbanded",
            LogoUrl = "https://2b2t.miraheze.org/wiki/Special:Redirect/file/Valkyria_Banner.png",
            LogoSourceUrl = "https://2b2t.miraheze.org/wiki/Valkyria", DateAddedUtc = DateTime.UtcNow.ToString("o"),
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new GroupSeeder(fixture.Context, NullLogger<GroupSeeder>.Instance).SeedAsync();

        var donFuer = await fixture.Context.Groups.SingleAsync(group => group.Name == "DonFuer",
            TestContext.Current.CancellationToken);
        Assert.Equal("https://2b2t.wikioasis.org/wiki/DonFuer", donFuer.WikiUrl);
        Assert.Equal("Operator-reviewed history", donFuer.Description);
        var valkyria = await fixture.Context.Groups.SingleAsync(group => group.Name == "Valkyria",
            TestContext.Current.CancellationToken);
        Assert.Equal("https://static.wikitide.net/2b2twiki/3/36/Valkyria_Mashup.png", valkyria.LogoUrl);
        Assert.Equal("Operator-reviewed faction history", valkyria.Description);
    }

    [Fact]
    public async Task Full_wiki_audit_seeds_new_groups_and_preserves_specific_roles()
    {
        await using var fixture = await Fixture.CreateAsync();
        var everyoneBase = fixture.AddLocation("Everyone Base");
        var fortAqua = fixture.AddLocation("Fort Aqua");
        var anthem = fixture.AddLocation("Anthem");
        var asgard = fixture.AddLocation("Asgard 2");
        var shenendoah = fixture.AddLocation("Shenendoah");
        var spookFour = fixture.AddLocation("Spook Base 4");
        var donBlaahaj = fixture.AddLocation("Don Blaahaj");
        var thotsburg = fixture.AddLocation("Thotsburg");
        var newmelonHamlet = fixture.AddLocation("Newmelon Hamlet");
        var laCapital = fixture.AddLocation("La Capital");
        var nuremberg = fixture.AddLocation("Nuremberg");
        var outpost = fixture.AddLocation("The Outpost");
        var twoK = fixture.AddLocation("2k2k");
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new GroupSeeder(fixture.Context, NullLogger<GroupSeeder>.Instance).SeedAsync();

        var links = await fixture.Context.LocationGroups.Include(link => link.Group)
            .ToListAsync(TestContext.Current.CancellationToken);
        Assert.Contains(links, link => link.LocationRowid == everyoneBase.Rowid && link.Group.Name == "EveryoneBase");
        Assert.Contains(links, link => link.LocationRowid == fortAqua.Rowid && link.Group.Name == "JIDF");
        Assert.Contains(links, link => link.LocationRowid == anthem.Rowid && link.Group.Name == "The Enclave");
        Assert.Contains(links, link => link.LocationRowid == asgard.Rowid && link.Group.Name == "The Legion of Shenandoah");
        Assert.Contains(links, link => link.LocationRowid == shenendoah.Rowid && link.Group.Name == "The Legion of Shenandoah");
        Assert.Contains(links, link => link.LocationRowid == spookFour.Rowid && link.Group.Name == "Infinity Incursion");
        Assert.Contains(links, link => link.LocationRowid == donBlaahaj.Rowid && link.Group.Name == "DonFuer");
        Assert.Contains(links, link => link.LocationRowid == thotsburg.Rowid && link.Group.Name == "The Society Project");
        Assert.Contains(links, link => link.LocationRowid == newmelonHamlet.Rowid && link.Group.Name == "The Society Project");
        Assert.Contains(links, link => link.LocationRowid == laCapital.Rowid && link.Group.Name == "The Republic");
        Assert.Contains(links, link => link.LocationRowid == nuremberg.Rowid && link.Group.Name == "The Republic");
        Assert.Contains(links, link => link.LocationRowid == outpost.Rowid && link.Group.Name == "United States of Hakle");
        Assert.Contains(links, link => link.LocationRowid == twoK.Rowid &&
            link.Group.Name == "New Facepunch Republic" && link.Role == "Builder / restorer");
    }

    [Fact]
    public async Task Canonical_audit_backfills_only_blank_group_prose_and_color()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Context.Groups.AddRange(
            new ServerGroup
            {
                Name = "DGA", Type = "Other", Description = null, Color = null,
                WikiUrl = "https://2b2t.wikioasis.org/wiki/Democratic_Group_Alliance",
                LogoSourceUrl = "https://2b2t.wikioasis.org/wiki/File:SGAv2_Banner.png",
                Founded = "July 20, 2019", Status = "Inactive",
                DateAddedUtc = DateTime.UtcNow.ToString("o"),
            },
            new ServerGroup
            {
                Name = "The Republic", Type = "Other", Description = "Operator-reviewed Republic history",
                Color = "#123456", WikiUrl = "https://2b2t.wikioasis.org/wiki/The_Republic",
                LogoSourceUrl = "https://2b2t.wikioasis.org/wiki/File:Greece.png",
                Founded = "December 2017", Status = "Active",
                DateAddedUtc = DateTime.UtcNow.ToString("o"),
            });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new GroupSeeder(fixture.Context, NullLogger<GroupSeeder>.Instance).SeedAsync();

        var dga = await fixture.Context.Groups.SingleAsync(group => group.Name == "DGA",
            TestContext.Current.CancellationToken);
        Assert.Contains("Small Groups Alliance", dga.Description);
        Assert.Equal("#486b9b", dga.Color);
        var republic = await fixture.Context.Groups.SingleAsync(group => group.Name == "The Republic",
            TestContext.Current.CancellationToken);
        Assert.Equal("Operator-reviewed Republic history", republic.Description);
        Assert.Equal("#123456", republic.Color);
    }

    [Fact]
    public async Task Canonical_audit_backfills_only_blank_structured_group_metadata()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Context.Groups.Add(new ServerGroup
        {
            Name = "Crimson Star", Type = "Other", Description = "Operator-reviewed Crimson Star history",
            Color = "#123456", Status = "Operator-reviewed status",
            WebsiteUrl = "https://operator.example/", DateAddedUtc = DateTime.UtcNow.ToString("o"),
        });
        fixture.Context.Groups.Add(new ServerGroup
        {
            Name = "The Society Project", Type = "Other", Description = "Operator-reviewed Society history",
            Color = "#654321", Status = "Operator-reviewed Society status",
            WikiUrl = "https://2b2t.wikioasis.org/wiki/The_Society_Project",
            DateAddedUtc = DateTime.UtcNow.ToString("o"),
        });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new GroupSeeder(fixture.Context, NullLogger<GroupSeeder>.Instance).SeedAsync();

        var crimsonStar = await fixture.Context.Groups.SingleAsync(group => group.Name == "Crimson Star",
            TestContext.Current.CancellationToken);
        Assert.Equal("Operator-reviewed Crimson Star history", crimsonStar.Description);
        Assert.Equal("#123456", crimsonStar.Color);
        Assert.Equal("Operator-reviewed status", crimsonStar.Status);
        Assert.Equal("https://operator.example/", crimsonStar.WebsiteUrl);
        Assert.Equal("August 21, 2020", crimsonStar.Founded);
        Assert.Equal("https://2b2t.wikioasis.org/wiki/Crimson_Star", crimsonStar.WikiUrl);
        Assert.Equal("https://static.wikitide.net/2b2twiki/2/26/Crimson_Star_Discord_Icon.png", crimsonStar.LogoUrl);
        Assert.Equal("https://2b2t.wikioasis.org/wiki/Crimson_Star", crimsonStar.LogoSourceUrl);

        var society = await fixture.Context.Groups.SingleAsync(group => group.Name == "The Society Project",
            TestContext.Current.CancellationToken);
        Assert.Equal("Operator-reviewed Society history", society.Description);
        Assert.Equal("#654321", society.Color);
        Assert.Equal("Operator-reviewed Society status", society.Status);
        Assert.Equal("https://2b2t.wikioasis.org/wiki/The_Society_Project", society.WikiUrl);
        Assert.Equal("June 6, 2017", society.Founded);
        Assert.Equal("https://static.wikitide.net/2b2twiki/1/1f/Society-logo.png", society.LogoUrl);
        Assert.Equal("https://2b2t.wikioasis.org/wiki/The_Society", society.LogoSourceUrl);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public AtlasContext Context { get; }

        private Fixture(SqliteConnection connection, AtlasContext context)
        {
            _connection = connection;
            Context = context;
        }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var context = new AtlasContext(new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new Fixture(connection, context);
        }

        public ServerLocation AddLocation(string name)
        {
            var location = new ServerLocation
            {
                LocationUuid = Guid.NewGuid().ToString(), Name = name, X = 0, Y = 64, Z = 0,
                Dimension = 0, DateAddedUtc = DateTime.UtcNow.ToString("o"),
            };
            Context.Locations.Add(location);
            return location;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
