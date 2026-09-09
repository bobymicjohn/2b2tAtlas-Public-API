using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using _2b2tAtlas.Server.Models;
using _2b2tAtlas.Server.Services;

namespace Atlas.Ingestor.Tests;

public sealed class SchemaUpgraderTests
{
    [Fact]
    public async Task Upgrade_makes_discord_handle_nullable_and_is_idempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<AtlasContext>().UseSqlite(connection).Options;
        await using var context = new AtlasContext(options);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        await context.Database.ExecuteSqlRawAsync("""
            DROP INDEX "IX_Warps_ArchiveSha256";
            ALTER TABLE "Warps" DROP COLUMN "ArchiveSha256";
            ALTER TABLE "Warps" DROP COLUMN "WorldDownloadDate";
            ALTER TABLE "Warps" DROP COLUMN "Source";
            ALTER TABLE "Renders" DROP COLUMN "Source";
            ALTER TABLE "Renders" DROP COLUMN "IsPublic";
            ALTER TABLE "Renders" DROP COLUMN "EquivalentToRenderId";
            ALTER TABLE "Locations" DROP COLUMN "ModifiedUtc";
            ALTER TABLE "Attachments" DROP COLUMN "MediaType";
            ALTER TABLE "Attachments" DROP COLUMN "ThumbnailPath";
            ALTER TABLE "Attachments" DROP COLUMN "SourceUrl";
            ALTER TABLE "Attachments" DROP COLUMN "Caption";
            ALTER TABLE "Attachments" DROP COLUMN "Attribution";
            DROP TABLE "LocationGroups";
            DROP TABLE "HighwayGroups";
            ALTER TABLE "Groups" DROP COLUMN "WebsiteUrl";
            ALTER TABLE "Groups" DROP COLUMN "DiscordUrl";
            ALTER TABLE "Groups" DROP COLUMN "LogoUrl";
            ALTER TABLE "Groups" DROP COLUMN "LogoSourceUrl";
            ALTER TABLE "Groups" DROP COLUMN "Founded";
            ALTER TABLE "Groups" DROP COLUMN "Status";
            ALTER TABLE "Groups" DROP COLUMN "ModifiedUtc";
            ALTER TABLE "IngestionJobs" DROP COLUMN "ArchiveWarpName";
            ALTER TABLE "IngestionJobs" DROP COLUMN "OriginalFileName";
            ALTER TABLE "IngestionJobs" DROP COLUMN "UseArchiveLastPlayed";
            ALTER TABLE "IngestionJobs" DROP COLUMN "ArchiveWarpSource";
            ALTER TABLE "IngestionJobs" DROP COLUMN "WarpId";
            ALTER TABLE "IngestionJobs" DROP COLUMN "MatchDecision";
            ALTER TABLE "IngestionJobs" DROP COLUMN "MatchConfidence";
            ALTER TABLE "IngestionJobs" DROP COLUMN "MatchReason";
            ALTER TABLE "Users" ADD COLUMN "DiscordHandleRequired" TEXT NOT NULL DEFAULT '';
            UPDATE "Users" SET "DiscordHandleRequired" = COALESCE("DiscordHandle", '');
            ALTER TABLE "Users" DROP COLUMN "DiscordHandle";
            ALTER TABLE "Users" RENAME COLUMN "DiscordHandleRequired" TO "DiscordHandle";
            INSERT INTO "Users" ("Username", "DiscordHandle", "PasswordHash", "Role", "IsActive", "IsAdmin", "CreatedAt",
                "IsSuperAdmin", "TrustLevel", "AutoApprove", "FailedLoginAttempts")
            VALUES ('blank-discord', '   ', 'hash', 'User', 1, 0, '2026-08-19T00:00:00Z', 0, 0, 0, 0);
            """, TestContext.Current.CancellationToken);

        var upgrader = new SchemaUpgrader(context, NullLogger<SchemaUpgrader>.Instance);
        await upgrader.UpgradeAsync();
        await upgrader.UpgradeAsync();

        await using var schema = connection.CreateCommand();
        schema.CommandText = "SELECT \"notnull\" FROM pragma_table_info('Users') WHERE name = 'DiscordHandle';";
        Assert.Equal(0L, Convert.ToInt64(await schema.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var value = connection.CreateCommand();
        value.CommandText = "SELECT DiscordHandle FROM Users WHERE Username = 'blank-discord';";
        Assert.Equal(DBNull.Value, await value.ExecuteScalarAsync(TestContext.Current.CancellationToken));

        await using var columns = connection.CreateCommand();
        columns.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('IngestionJobs')
            WHERE name IN ('OriginalFileName','UseArchiveLastPlayed','ArchiveWarpName','ArchiveWarpSource','WarpId','MatchDecision','MatchConfidence','MatchReason');
            """;
        Assert.Equal(8L, Convert.ToInt64(await columns.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var warpSchema = connection.CreateCommand();
        warpSchema.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('Warps')
            WHERE name IN ('ArchiveSha256','WorldDownloadDate','Source');
            """;
        Assert.Equal(3L, Convert.ToInt64(await warpSchema.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var warpIndex = connection.CreateCommand();
        warpIndex.CommandText = "SELECT COUNT(*) FROM pragma_index_list('Warps') WHERE name = 'IX_Warps_ArchiveSha256' AND \"unique\" = 1;";
        Assert.Equal(1L, Convert.ToInt64(await warpIndex.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var renderSchema = connection.CreateCommand();
        renderSchema.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Renders') WHERE name IN ('Source','ArchiveWarpId','IsPublic','EquivalentToRenderId');";
        Assert.Equal(4L, Convert.ToInt64(await renderSchema.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var locationSchema = connection.CreateCommand();
        locationSchema.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Locations') WHERE name = 'ModifiedUtc';";
        Assert.Equal(1L, Convert.ToInt64(await locationSchema.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var attachmentSchema = connection.CreateCommand();
        attachmentSchema.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('Attachments')
            WHERE name IN ('MediaType','ThumbnailPath','SourceUrl','Caption','Attribution');
            """;
        Assert.Equal(5L, Convert.ToInt64(await attachmentSchema.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var groupSchema = connection.CreateCommand();
        groupSchema.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('Groups')
            WHERE name IN ('WebsiteUrl','DiscordUrl','LogoUrl','LogoSourceUrl','Founded','Status','ModifiedUtc');
            """;
        Assert.Equal(7L, Convert.ToInt64(await groupSchema.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var locationGroupTable = connection.CreateCommand();
        locationGroupTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'LocationGroups';";
        Assert.Equal(1L, Convert.ToInt64(await locationGroupTable.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var locationGroupIndex = connection.CreateCommand();
        locationGroupIndex.CommandText = "SELECT COUNT(*) FROM pragma_index_list('LocationGroups') WHERE name = 'IX_LocationGroups_GroupId';";
        Assert.Equal(1L, Convert.ToInt64(await locationGroupIndex.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var highwayGroupTable = connection.CreateCommand();
        highwayGroupTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'HighwayGroups';";
        Assert.Equal(1L, Convert.ToInt64(await highwayGroupTable.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var highwayGroupIndex = connection.CreateCommand();
        highwayGroupIndex.CommandText = "SELECT COUNT(*) FROM pragma_index_list('HighwayGroups') WHERE name = 'IX_HighwayGroups_GroupId';";
        Assert.Equal(1L, Convert.ToInt64(await highwayGroupIndex.ExecuteScalarAsync(TestContext.Current.CancellationToken)));

        await using var renderIndex = connection.CreateCommand();
        renderIndex.CommandText = "SELECT COUNT(*) FROM pragma_index_list('Renders') WHERE name = 'IX_Renders_ArchiveWarpId' AND \"unique\" = 1;";
        Assert.Equal(1L, Convert.ToInt64(await renderIndex.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }
}
