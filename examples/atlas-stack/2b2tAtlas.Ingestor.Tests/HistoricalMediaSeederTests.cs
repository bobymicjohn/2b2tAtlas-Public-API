using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;
using ServerLocation = _2b2tAtlas.Server.Models.Location;

namespace Atlas.Ingestor.Tests;

public sealed class HistoricalMediaSeederTests
{
    [Theory]
    [InlineData("youtube-location-media.json")]
    [InlineData("reference-location-media.json")]
    public async Task Shared_video_can_belong_to_two_locations_without_duplicate_or_mismatched_rows(string manifestName)
    {
        var directory = Path.Combine(Path.GetTempPath(), "atlas-media-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var context = new AtlasContext(new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var locations = new[] { "First Base", "Second Base" }.Select(name => new ServerLocation
            {
                Name = name, LocationUuid = Guid.NewGuid().ToString(), DateAddedUtc = DateTime.UtcNow.ToString("o"),
            }).ToArray();
            context.Locations.AddRange(locations);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            const string url = "https://www.youtube.com/watch?v=GoH5ztuUmzU";
            const string shareUrl = "https://youtu.be/GoH5ztuUmzU?t=12";
            context.Attachments.Add(new Attachment { LocationRowid = locations[0].Rowid, FileName = "Operator link", FilePath = shareUrl });
            locations[0].VideoUrl = "https://www.youtube.com/watch?v=B-JUzsfrjXo";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var rows = new[]
            {
                new { locationRowid = locations[0].Rowid, locationName = locations[0].Name, fileName = "Tour", mediaType = "Video", path = url },
                new { locationRowid = locations[1].Rowid, locationName = locations[1].Name, fileName = "Tour", mediaType = "Video", path = url },
                new { locationRowid = locations[1].Rowid, locationName = locations[1].Name, fileName = "Same tour, later", mediaType = "Video", path = url + "&t=350s" },
                new { locationRowid = locations[0].Rowid, locationName = locations[0].Name, fileName = "Already primary", mediaType = "Video", path = "https://www.youtube.com/embed/B-JUzsfrjXo" },
                new { locationRowid = locations[1].Rowid, locationName = "Wrong Base", fileName = "Wrong", mediaType = "Video", path = "https://example.test/wrong" },
            };
            await File.WriteAllTextAsync(Path.Combine(directory, manifestName), JsonSerializer.Serialize(rows), TestContext.Current.CancellationToken);
            var seeder = new HistoricalMediaSeeder(context, null!, NullLogger<HistoricalMediaSeeder>.Instance);
            await seeder.SeedAsync(directory);
            await seeder.SeedAsync(directory);
            var saved = await context.Attachments.OrderBy(item => item.LocationRowid).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, saved.Count);
            Assert.Equal("Operator link", saved[0].FileName);
            Assert.Equal(locations[1].Rowid, saved[1].LocationRowid);
            Assert.Equal(shareUrl, saved[0].FilePath);
            Assert.Equal(url, saved[1].FilePath);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Assert.StartsWith(prefix, Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(directory, recursive: true);
        }
    }
}
