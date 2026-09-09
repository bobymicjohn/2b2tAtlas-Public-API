using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Services;

/// <summary>
/// Applies additive, idempotent schema changes to the existing SQLite database.
/// The app uses <c>EnsureCreated</c> (no EF migrations), which never alters an
/// already-created database — so new columns on existing tables must be added
/// with guarded <c>ALTER TABLE ... ADD COLUMN</c> statements. Safe to run on
/// every startup: each column is only added if it is missing.
/// </summary>
public class SchemaUpgrader
{
    private readonly AtlasContext _context;
    private readonly ILogger<SchemaUpgrader> _logger;

    /// <summary>Initializes the additive SQLite schema upgrader.</summary>
    /// <param name="context">The context whose underlying database connection will be upgraded.</param>
    /// <param name="logger">The logger for applied columns, tables, indexes, and data migrations.</param>
    public SchemaUpgrader(AtlasContext context, ILogger<SchemaUpgrader> logger)
    {
        _context = context;
        _logger = logger;
    }

    /// <summary>Applies all required idempotent schema and compatibility upgrades in dependency order.</summary>
    /// <returns>A task that completes after the database is ready for the current server model.</returns>
    /// <remarks>
    /// The method may create tables and indexes, add guarded columns, normalize legacy roles and
    /// dimensions, and backfill stable location UUIDs. A connection opened here is closed before return.
    /// </remarks>
    public async Task UpgradeAsync()
    {
        var conn = _context.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
            opened = true;
        }

        try
        {
            var userColumns = await GetColumnsAsync(conn, "Users");

            // Login identity migrated from email to Discord handle.
            await RenameColumnIfNeededAsync(conn, userColumns, "Users", "Email", "DiscordHandle");
            await MakeColumnNullableIfNeededAsync(conn, "Users", "DiscordHandle");

            // RBAC / profile columns added by GAMEPLAN §15 (Phase 2E).
            await AddColumnIfMissingAsync(conn, userColumns, "Users", "IsSuperAdmin", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, userColumns, "Users", "MinecraftUsername", "TEXT");
            await AddColumnIfMissingAsync(conn, userColumns, "Users", "DiscordId", "TEXT");
            await AddColumnIfMissingAsync(conn, userColumns, "Users", "Bio", "TEXT");
            await AddColumnIfMissingAsync(conn, userColumns, "Users", "TrustLevel", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, userColumns, "Users", "AutoApprove", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, userColumns, "Users", "LastEditAt", "TEXT");
            await AddColumnIfMissingAsync(conn, userColumns, "Users", "FailedLoginAttempts", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, userColumns, "Users", "LastFailedLoginAt", "TEXT");
            await AddColumnIfMissingAsync(conn, userColumns, "Users", "LockoutEndUtc", "TEXT");

            // Highways table (GAMEPLAN §14). Created here because EnsureCreated
            // never adds new tables to an already-created database.
            await CreateHighwaysTableAsync(conn);
            await CreateSchemaMigrationsTableAsync(conn);
            await MigrateLegacyRolesAsync(conn);
            await MigrateHighwayDimensionContractAsync(conn);
            await CreateAuditLogsTableAsync(conn);
            await CreateGroupsTableAsync(conn);
            var groupColumns = await GetColumnsAsync(conn, "Groups");
            await AddColumnIfMissingAsync(conn, groupColumns, "Groups", "WebsiteUrl", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, groupColumns, "Groups", "DiscordUrl", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, groupColumns, "Groups", "LogoUrl", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, groupColumns, "Groups", "LogoSourceUrl", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, groupColumns, "Groups", "Founded", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, groupColumns, "Groups", "Status", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, groupColumns, "Groups", "ModifiedUtc", "TEXT NULL");
            await BackfillGroupModifiedUtcAsync(conn);
            await CreateLocationGroupsTableAsync(conn);
            await CreateHighwayGroupsTableAsync(conn);
            await CreateRolePermissionsTableAsync(conn);
            await CreateRevisionsTableAsync(conn);
            var revisionColumns = await GetColumnsAsync(conn, "Revisions");
            await AddColumnIfMissingAsync(conn, revisionColumns, "Revisions", "Source", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, revisionColumns, "Revisions", "Confidence", "REAL NULL");
            await AddColumnIfMissingAsync(conn, revisionColumns, "Revisions", "PreviousJson", "TEXT NULL");
            await CreateMapRendersTableAsync(conn);
            await CreateIngestionJobsTableAsync(conn);
            var locationColumns = await GetColumnsAsync(conn, "Locations");
            await AddColumnIfMissingAsync(conn, locationColumns, "Locations", "LocationUuid", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, locationColumns, "Locations", "Tags", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, locationColumns, "Locations", "Wiki", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, locationColumns, "Locations", "VideoUrl", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, locationColumns, "Locations", "ModifiedUtc", "TEXT NULL");
            await BackfillLocationUuidsAsync(conn);
            await BackfillLocationModifiedUtcAsync(conn);
            var attachmentColumns = await GetColumnsAsync(conn, "Attachments");
            await AddColumnIfMissingAsync(conn, attachmentColumns, "Attachments", "MediaType", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, attachmentColumns, "Attachments", "ThumbnailPath", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, attachmentColumns, "Attachments", "SourceUrl", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, attachmentColumns, "Attachments", "Caption", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, attachmentColumns, "Attachments", "Attribution", "TEXT NULL");
            var ingestionColumns = await GetColumnsAsync(conn, "IngestionJobs");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "ClaimTokenSha256", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "AttemptCount", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "LeaseExpiresUtc", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "ExistingLocationId", "INTEGER NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "RenderId", "INTEGER NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "ProgressPercent", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "EtaSeconds", "INTEGER NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "InspectionJson", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "ArchiveEvidenceJson", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "OriginalFileName", "TEXT NULL");
            // Preserve dates on legacy/completed jobs. Newly queued jobs set this explicitly from the request.
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "UseArchiveLastPlayed", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "ArchiveWarpName", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "ArchiveWarpX", "REAL NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "ArchiveWarpY", "REAL NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "ArchiveWarpZ", "REAL NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "ArchiveWarpSource", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "WarpId", "INTEGER NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "MatchDecision", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "MatchConfidence", "REAL NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "MatchReason", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "Dimension", "TEXT NOT NULL DEFAULT 'overworld'");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "WorldRoot", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "MatchResolved", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "MatchSuggestionsJson", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "RerenderRequested", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, ingestionColumns, "IngestionJobs", "RenderTopY", "INTEGER NULL");
            await MigrateIngestionJobSlugDimensionIndexAsync(conn);

            var renderColumns = await GetColumnsAsync(conn, "Renders");
            await AddColumnIfMissingAsync(conn, renderColumns, "Renders", "MinX", "INTEGER NULL");
            await AddColumnIfMissingAsync(conn, renderColumns, "Renders", "MinZ", "INTEGER NULL");
            await AddColumnIfMissingAsync(conn, renderColumns, "Renders", "MaxXExclusive", "INTEGER NULL");
            await AddColumnIfMissingAsync(conn, renderColumns, "Renders", "MaxZExclusive", "INTEGER NULL");
            await AddColumnIfMissingAsync(conn, renderColumns, "Renders", "MaxNativeZoom", "INTEGER NULL");
            await AddColumnIfMissingAsync(conn, renderColumns, "Renders", "CoordinateScheme", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, renderColumns, "Renders", "HasDayNight", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, renderColumns, "Renders", "Source", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, renderColumns, "Renders", "ArchiveWarpId", "INTEGER NULL");
            await AddColumnIfMissingAsync(conn, renderColumns, "Renders", "IsPublic", "INTEGER NOT NULL DEFAULT 1");
            await AddColumnIfMissingAsync(conn, renderColumns, "Renders", "EquivalentToRenderId", "INTEGER NULL");
            await CreateUniqueRenderTilesPathIndexAsync(conn);

            var warpColumns = await GetColumnsAsync(conn, "Warps");
            await AddColumnIfMissingAsync(conn, warpColumns, "Warps", "ArchiveSha256", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, warpColumns, "Warps", "WorldDownloadDate", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, warpColumns, "Warps", "Source", "TEXT NULL");
            await AddColumnIfMissingAsync(conn, warpColumns, "Warps", "ArchiveX", "REAL NULL");
            await AddColumnIfMissingAsync(conn, warpColumns, "Warps", "ArchiveY", "REAL NULL");
            await AddColumnIfMissingAsync(conn, warpColumns, "Warps", "ArchiveZ", "REAL NULL");
            await CreateUniqueWarpArchiveIndexAsync(conn);
            await MigrateArchiveRenderMetadataAsync(conn);
            await CreateUniqueRenderArchiveWarpIndexAsync(conn);

            // Attribution FK column added to an existing Highways table.
            var highwayColumns = await GetColumnsAsync(conn, "Highways");
            await AddColumnIfMissingAsync(conn, highwayColumns, "Highways", "BuilderGroupId", "INTEGER NULL");
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    private static async Task CreateSchemaMigrationsTableAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""SchemaMigrations"" (
    ""Name"" TEXT NOT NULL CONSTRAINT ""PK_SchemaMigrations"" PRIMARY KEY,
    ""AppliedUtc"" TEXT NOT NULL
);";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateUniqueRenderTilesPathIndexAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Renders_TilesPath""
ON ""Renders"" (""TilesPath"")
WHERE ""TilesPath"" IS NOT NULL AND ""TilesPath"" <> '';";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task BackfillLocationModifiedUtcAsync(DbConnection conn)
    {
        // The canonical static entity template and structured data changed for every record on this
        // date. That is a significant page change, so it is an honest initial sitemap lastmod floor.
        const string semanticEntityUpgradeUtc = "2026-08-31T00:00:00.0000000Z";
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE ""Locations""
SET ""ModifiedUtc"" = CASE
    WHEN ""DateAddedUtc"" IS NOT NULL
      AND ""DateAddedUtc"" <> ''
      AND julianday(""DateAddedUtc"") > julianday($semanticUpgrade)
        THEN ""DateAddedUtc""
    ELSE $semanticUpgrade
END
WHERE ""ModifiedUtc"" IS NULL OR ""ModifiedUtc"" = '';";
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "$semanticUpgrade";
        parameter.Value = semanticEntityUpgradeUtc;
        cmd.Parameters.Add(parameter);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task BackfillGroupModifiedUtcAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
UPDATE ""Groups""
SET ""ModifiedUtc"" = COALESCE(NULLIF(""DateAddedUtc"", ''), $now)
WHERE ""ModifiedUtc"" IS NULL OR ""ModifiedUtc"" = '';";
        var now = cmd.CreateParameter();
        now.ParameterName = "$now";
        now.Value = DateTime.UtcNow.ToString("o");
        cmd.Parameters.Add(now);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateUniqueWarpArchiveIndexAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Warps_ArchiveSha256""
ON ""Warps"" (""ArchiveSha256"")
WHERE ""ArchiveSha256"" IS NOT NULL AND ""ArchiveSha256"" <> '';";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateUniqueRenderArchiveWarpIndexAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Renders_ArchiveWarpId""
ON ""Renders"" (""ArchiveWarpId"")
WHERE ""ArchiveWarpId"" IS NOT NULL;";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task MigrateArchiveRenderMetadataAsync(DbConnection conn)
    {
        const string migrationName = "20260831_RenderSourceWarpAndArchiveNames";
        await using var transaction = await conn.BeginTransactionAsync();

        using (var check = conn.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM \"SchemaMigrations\" WHERE \"Name\" = $name;";
            var parameter = check.CreateParameter();
            parameter.ParameterName = "$name";
            parameter.Value = migrationName;
            check.Parameters.Add(parameter);
            if (Convert.ToInt64(await check.ExecuteScalarAsync()) != 0)
            {
                await transaction.CommitAsync();
                return;
            }
        }

        var renderNames = new Dictionary<int, string>();
        using (var selectRenders = conn.CreateCommand())
        {
            selectRenders.Transaction = transaction;
            selectRenders.CommandText = @"
SELECT RenderId, ArchiveWarpName
FROM IngestionJobs
WHERE RenderId IS NOT NULL
  AND ArchiveWarpName IS NOT NULL
  AND Source LIKE 'The Archive automated sync%'
ORDER BY Id;";
            await using var reader = await selectRenders.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var displayName = Atlas.ArchiveWarpResolver.DisplayIdentity(reader.GetString(1));
                if (displayName.Length > 0) renderNames[reader.GetInt32(0)] = displayName;
            }
        }

        var locationNames = new Dictionary<int, string>();
        using (var selectLocations = conn.CreateCommand())
        {
            selectLocations.Transaction = transaction;
            selectLocations.CommandText = @"
SELECT j.ExistingLocationId, j.ArchiveWarpName
FROM IngestionJobs j
JOIN Locations l ON l.Rowid = j.ExistingLocationId
WHERE j.ExistingLocationId IS NOT NULL
  AND j.ArchiveWarpName IS NOT NULL
  AND j.Source LIKE 'The Archive automated sync%'
  AND j.MatchDecision IN ('new', 'manual-new', 'new-rehome')
  AND l.Description LIKE 'Base render ingested from The Archive automated sync%'
ORDER BY j.Id;";
            await using var reader = await selectLocations.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var displayName = Atlas.ArchiveWarpResolver.DisplayIdentity(reader.GetString(1));
                if (displayName.Length > 0) locationNames[reader.GetInt32(0)] = displayName;
            }
        }

        // A render belongs to the Atlas location, while its separate date and exact warp identify the
        // historical WDL. Reuse the location's canonical display name instead of leaking catalog suffixes.
        using (var selectRenderLocations = conn.CreateCommand())
        {
            selectRenderLocations.Transaction = transaction;
            selectRenderLocations.CommandText = @"
SELECT j.RenderId, l.Rowid, l.Name
FROM IngestionJobs j
JOIN Renders r ON r.Id = j.RenderId
JOIN Locations l ON l.Rowid = r.LocationRowid
WHERE j.RenderId IS NOT NULL
  AND j.Source LIKE 'The Archive automated sync%'
ORDER BY j.Id;";
            await using var reader = await selectRenderLocations.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var locationId = reader.GetInt32(1);
                var displayName = locationNames.GetValueOrDefault(locationId, reader.GetString(2));
                if (!string.IsNullOrWhiteSpace(displayName)) renderNames[reader.GetInt32(0)] = displayName;
            }
        }

        using (var backfill = conn.CreateCommand())
        {
            backfill.Transaction = transaction;
            backfill.CommandText = @"
UPDATE Renders
SET Source = COALESCE((
    SELECT CASE
        WHEN j.Source LIKE 'The Archive automated sync%' THEN 'archive-collector'
        WHEN j.RequestedByUserId IS NOT NULL THEN 'manual-upload'
        ELSE 'wdl-ingestion'
    END
    FROM IngestionJobs j
    WHERE j.RenderId = Renders.Id
    ORDER BY j.Id DESC
    LIMIT 1
), 'legacy')
WHERE Source IS NULL OR Source = '';

WITH latest AS (
    SELECT WarpId, MAX(Id) AS JobId
    FROM IngestionJobs
    WHERE WarpId IS NOT NULL AND RenderId IS NOT NULL
    GROUP BY WarpId
)
UPDATE Renders
SET ArchiveWarpId = (
    SELECT j.WarpId
    FROM IngestionJobs j
    JOIN latest l ON l.JobId = j.Id
    WHERE j.RenderId = Renders.Id
)
WHERE Id IN (
    SELECT j.RenderId
    FROM IngestionJobs j
    JOIN latest l ON l.JobId = j.Id
);

UPDATE Locations
SET Description = NULL
WHERE Description LIKE 'Base render ingested from The Archive automated sync%';

UPDATE Renders
SET Description = NULL
WHERE Description LIKE 'Rendered from The Archive automated sync%';";
            await backfill.ExecuteNonQueryAsync();
        }

        foreach (var pair in renderNames)
            await UpdateNameAsync(conn, transaction, "Renders", "Id", pair.Key, pair.Value);
        foreach (var pair in locationNames)
            await UpdateNameAsync(conn, transaction, "Locations", "Rowid", pair.Key, pair.Value);

        using (var record = conn.CreateCommand())
        {
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO \"SchemaMigrations\" (\"Name\", \"AppliedUtc\") VALUES ($name, $utc);";
            var name = record.CreateParameter();
            name.ParameterName = "$name";
            name.Value = migrationName;
            record.Parameters.Add(name);
            var utc = record.CreateParameter();
            utc.ParameterName = "$utc";
            utc.Value = DateTime.UtcNow.ToString("o");
            record.Parameters.Add(utc);
            await record.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        _logger.LogInformation(
            "SchemaUpgrader: linked render provenance/warps and cleaned {RenderCount} render names plus {LocationCount} Archive-created location names.",
            renderNames.Count, locationNames.Count);
    }

    private static async Task UpdateNameAsync(
        DbConnection conn,
        DbTransaction transaction,
        string table,
        string keyColumn,
        int id,
        string name)
    {
        using var update = conn.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE \"{table}\" SET \"Name\" = $name WHERE \"{keyColumn}\" = $id;";
        var nameParameter = update.CreateParameter();
        nameParameter.ParameterName = "$name";
        nameParameter.Value = name;
        update.Parameters.Add(nameParameter);
        var idParameter = update.CreateParameter();
        idParameter.ParameterName = "$id";
        idParameter.Value = id;
        update.Parameters.Add(idParameter);
        await update.ExecuteNonQueryAsync();
    }

    private async Task MigrateIngestionJobSlugDimensionIndexAsync(DbConnection conn)
    {
        // A base slug hosts one active job per dimension (overworld/nether/end). Cancelled/failed jobs
        // release the reservation so the same slug + dimension can be re-uploaded, hence a partial index.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
DROP INDEX IF EXISTS ""IX_IngestionJobs_Slug"";
DROP INDEX IF EXISTS ""IX_IngestionJobs_Slug_Dimension"";
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_IngestionJobs_Slug_Dimension_Active""
ON ""IngestionJobs"" (""Slug"", ""Dimension"")
WHERE ""Status"" <> 'cancelled' AND ""Status"" <> 'failed';";
        await cmd.ExecuteNonQueryAsync();
        _logger.LogInformation("SchemaUpgrader: ensured partial unique index IX_IngestionJobs_Slug_Dimension_Active");
    }

    private async Task MigrateHighwayDimensionContractAsync(DbConnection conn)
    {
        const string migrationName = "20260809_HighwayNetherDimension1";
        await using var transaction = await conn.BeginTransactionAsync();

        using var check = conn.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM \"SchemaMigrations\" WHERE \"Name\" = $name;";
        var nameParameter = check.CreateParameter();
        nameParameter.ParameterName = "$name";
        nameParameter.Value = migrationName;
        check.Parameters.Add(nameParameter);
        if (Convert.ToInt64(await check.ExecuteScalarAsync()) != 0)
        {
            await transaction.CommitAsync();
            return;
        }

        // Every existing Highway uses Nether-coordinate geometry by contract.
        using var update = conn.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE \"Highways\" SET \"Dimension\" = 1 WHERE \"Dimension\" = 2;";
        var affected = await update.ExecuteNonQueryAsync();

        using var record = conn.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = "INSERT INTO \"SchemaMigrations\" (\"Name\", \"AppliedUtc\") VALUES ($name, $utc);";
        var recordName = record.CreateParameter();
        recordName.ParameterName = "$name";
        recordName.Value = migrationName;
        record.Parameters.Add(recordName);
        var utcParameter = record.CreateParameter();
        utcParameter.ParameterName = "$utc";
        utcParameter.Value = DateTime.UtcNow.ToString("o");
        record.Parameters.Add(utcParameter);
        await record.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        _logger.LogInformation("SchemaUpgrader: migrated {Count} highway rows to canonical Nether dimension 1.", affected);
    }

    private async Task MigrateLegacyRolesAsync(DbConnection conn)
    {
        const string migrationName = "20260809_CanonicalCommunityRoles";
        await using var transaction = await conn.BeginTransactionAsync();

        using var check = conn.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM \"SchemaMigrations\" WHERE \"Name\" = $name;";
        var nameParameter = check.CreateParameter();
        nameParameter.ParameterName = "$name";
        nameParameter.Value = migrationName;
        check.Parameters.Add(nameParameter);
        if (Convert.ToInt64(await check.ExecuteScalarAsync()) != 0)
        {
            await transaction.CommitAsync();
            return;
        }

        using var update = conn.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE \"Users\" SET \"Role\" = 'User', \"IsAdmin\" = 0 WHERE lower(\"Role\") = 'technician';";
        var affected = await update.ExecuteNonQueryAsync();

        using var record = conn.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = "INSERT INTO \"SchemaMigrations\" (\"Name\", \"AppliedUtc\") VALUES ($name, $utc);";
        var recordName = record.CreateParameter();
        recordName.ParameterName = "$name";
        recordName.Value = migrationName;
        record.Parameters.Add(recordName);
        var utcParameter = record.CreateParameter();
        utcParameter.ParameterName = "$utc";
        utcParameter.Value = DateTime.UtcNow.ToString("o");
        record.Parameters.Add(utcParameter);
        await record.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        _logger.LogInformation("SchemaUpgrader: migrated {Count} legacy Technician role(s) to Member.", affected);
    }

    private async Task CreateGroupsTableAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""Groups"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Groups"" PRIMARY KEY AUTOINCREMENT,
    ""Name"" TEXT NOT NULL,
    ""Type"" TEXT NOT NULL,
    ""Description"" TEXT NULL,
    ""Color"" TEXT NULL,
    ""WikiUrl"" TEXT NULL,
    ""WebsiteUrl"" TEXT NULL,
    ""DiscordUrl"" TEXT NULL,
    ""LogoUrl"" TEXT NULL,
    ""LogoSourceUrl"" TEXT NULL,
    ""Founded"" TEXT NULL,
    ""Status"" TEXT NULL,
    ""DateAddedUtc"" TEXT NOT NULL,
    ""ModifiedUtc"" TEXT NOT NULL
);";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateLocationGroupsTableAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""LocationGroups"" (
    ""LocationRowid"" INTEGER NOT NULL,
    ""GroupId"" INTEGER NOT NULL,
    ""Role"" TEXT NOT NULL DEFAULT 'Builder',
    ""DateAddedUtc"" TEXT NOT NULL,
    CONSTRAINT ""PK_LocationGroups"" PRIMARY KEY (""LocationRowid"", ""GroupId""),
    CONSTRAINT ""FK_LocationGroups_Locations"" FOREIGN KEY (""LocationRowid"") REFERENCES ""Locations"" (""Rowid"") ON DELETE CASCADE,
    CONSTRAINT ""FK_LocationGroups_Groups"" FOREIGN KEY (""GroupId"") REFERENCES ""Groups"" (""Id"") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ""IX_LocationGroups_GroupId"" ON ""LocationGroups"" (""GroupId"");";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task BackfillLocationUuidsAsync(DbConnection conn)
    {
        using (var recover = conn.CreateCommand())
        {
            recover.CommandText = @"
UPDATE ""Locations""
SET ""LocationUuid"" = (
    SELECT MIN(""LocationUuidFk"")
    FROM ""Warps""
    WHERE ""Warps"".""LocationRowid"" = ""Locations"".""Rowid""
      AND ""LocationUuidFk"" IS NOT NULL
      AND ""LocationUuidFk"" <> ''
)
WHERE ""LocationUuid"" IS NULL OR ""LocationUuid"" = '';";
            await recover.ExecuteNonQueryAsync();
        }

        var missing = new List<int>();
        using (var select = conn.CreateCommand())
        {
            select.CommandText = "SELECT \"Rowid\" FROM \"Locations\" WHERE \"LocationUuid\" IS NULL OR \"LocationUuid\" = '';";
            using var reader = await select.ExecuteReaderAsync();
            while (await reader.ReadAsync()) missing.Add(reader.GetInt32(0));
        }

        foreach (var rowid in missing)
        {
            using var update = conn.CreateCommand();
            update.CommandText = "UPDATE \"Locations\" SET \"LocationUuid\" = $uuid WHERE \"Rowid\" = $rowid;";
            var uuid = update.CreateParameter();
            uuid.ParameterName = "$uuid";
            uuid.Value = Guid.NewGuid().ToString();
            update.Parameters.Add(uuid);
            var id = update.CreateParameter();
            id.ParameterName = "$rowid";
            id.Value = rowid;
            update.Parameters.Add(id);
            await update.ExecuteNonQueryAsync();
        }

        using var index = conn.CreateCommand();
        index.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Locations_LocationUuid\" ON \"Locations\" (\"LocationUuid\");";
        await index.ExecuteNonQueryAsync();
    }

    private async Task CreateRolePermissionsTableAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""RolePermissions"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_RolePermissions"" PRIMARY KEY AUTOINCREMENT,
    ""Role"" TEXT NOT NULL,
    ""Permission"" TEXT NOT NULL
);";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateRevisionsTableAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""Revisions"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Revisions"" PRIMARY KEY AUTOINCREMENT,
    ""EntityType"" TEXT NOT NULL,
    ""EntityId"" INTEGER NOT NULL,
    ""ProposedJson"" TEXT NOT NULL,
    ""Note"" TEXT NULL,
    ""Status"" TEXT NOT NULL,
    ""SubmittedByUserId"" INTEGER NULL,
    ""SubmittedByUsername"" TEXT NULL,
    ""ReviewedByUserId"" INTEGER NULL,
    ""ReviewNote"" TEXT NULL,
    ""SubmittedUtc"" TEXT NOT NULL,
    ""ReviewedUtc"" TEXT NULL
);";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateMapRendersTableAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""MapRenders"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_MapRenders"" PRIMARY KEY AUTOINCREMENT,
    ""Slug"" TEXT NOT NULL,
    ""Name"" TEXT NOT NULL,
    ""Dimension"" INTEGER NOT NULL,
    ""Scale"" TEXT NOT NULL,
    ""UrlTemplate"" TEXT NOT NULL,
    ""HasDayNight"" INTEGER NOT NULL,
    ""MaxNativeZoom"" INTEGER NULL,
    ""WorldDownloadDate"" TEXT NULL,
    ""Source"" TEXT NULL,
    ""SortOrder"" INTEGER NOT NULL,
    ""IsPublished"" INTEGER NOT NULL,
    ""DateAddedUtc"" TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_MapRenders_Slug"" ON ""MapRenders"" (""Slug"");";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateIngestionJobsTableAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""IngestionJobs"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_IngestionJobs"" PRIMARY KEY AUTOINCREMENT,
    ""PublicId"" TEXT NOT NULL,
    ""IntakeFileName"" TEXT NOT NULL,
    ""Slug"" TEXT NOT NULL,
    ""Name"" TEXT NOT NULL,
    ""WorldDownloadDate"" TEXT NOT NULL,
    ""UseArchiveLastPlayed"" INTEGER NOT NULL DEFAULT 0,
    ""Source"" TEXT NOT NULL,
    ""Scale"" TEXT NOT NULL,
    ""DayNight"" INTEGER NOT NULL,
    ""ExistingLocationId"" INTEGER NULL,
    ""RenderId"" INTEGER NULL,
    ""Status"" TEXT NOT NULL,
    ""Stage"" TEXT NULL,
    ""Message"" TEXT NULL,
    ""ProgressPercent"" INTEGER NOT NULL DEFAULT 0,
    ""EtaSeconds"" INTEGER NULL,
    ""InspectionJson"" TEXT NULL,
    ""ArchiveEvidenceJson"" TEXT NULL,
    ""OriginalFileName"" TEXT NULL,
    ""ArchiveWarpName"" TEXT NULL,
    ""ArchiveWarpX"" REAL NULL,
    ""ArchiveWarpY"" REAL NULL,
    ""ArchiveWarpZ"" REAL NULL,
    ""ArchiveWarpSource"" TEXT NULL,
    ""WarpId"" INTEGER NULL,
    ""MatchDecision"" TEXT NULL,
    ""MatchConfidence"" REAL NULL,
    ""MatchReason"" TEXT NULL,
    ""ArchiveSha256"" TEXT NULL,
    ""ClaimTokenSha256"" TEXT NULL,
    ""AttemptCount"" INTEGER NOT NULL DEFAULT 0,
    ""RerenderRequested"" INTEGER NOT NULL DEFAULT 0,
    ""RenderTopY"" INTEGER NULL,
    ""RequestedByUserId"" INTEGER NULL,
    ""RequestedByUsername"" TEXT NULL,
    ""RequestedUtc"" TEXT NOT NULL,
    ""ClaimedUtc"" TEXT NULL,
    ""LeaseExpiresUtc"" TEXT NULL,
    ""UpdatedUtc"" TEXT NULL,
    ""CompletedUtc"" TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ""IX_IngestionJobs_PublicId"" ON ""IngestionJobs"" (""PublicId"");
CREATE INDEX IF NOT EXISTS ""IX_IngestionJobs_Status_Id"" ON ""IngestionJobs"" (""Status"", ""Id"");";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateAuditLogsTableAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""AuditLogs"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_AuditLogs"" PRIMARY KEY AUTOINCREMENT,
    ""Action"" TEXT NOT NULL,
    ""EntityType"" TEXT NOT NULL,
    ""EntityId"" INTEGER NOT NULL,
    ""UserId"" INTEGER NULL,
    ""Username"" TEXT NULL,
    ""Summary"" TEXT NULL,
    ""DetailsJson"" TEXT NULL,
    ""CreatedUtc"" TEXT NOT NULL
);";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task CreateHighwaysTableAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""Highways"" (
    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_Highways"" PRIMARY KEY AUTOINCREMENT,
    ""Name"" TEXT NOT NULL,
    ""Slug"" TEXT NOT NULL,
    ""Dimension"" INTEGER NOT NULL,
    ""Category"" TEXT NOT NULL,
    ""PointsJson"" TEXT NOT NULL,
    ""RingRadius"" INTEGER NULL,
    ""Width"" INTEGER NOT NULL,
    ""Height"" INTEGER NULL,
    ""YLevel"" INTEGER NULL,
    ""Paved"" INTEGER NOT NULL,
    ""PavingMaterial"" TEXT NOT NULL,
    ""Walls"" INTEGER NOT NULL,
    ""Enclosed"" INTEGER NOT NULL,
    ""IsRoofHighway"" INTEGER NOT NULL,
    ""Lit"" INTEGER NOT NULL,
    ""Status"" TEXT NOT NULL,
    ""LengthBlocks"" INTEGER NULL,
    ""Description"" TEXT NULL,
    ""WikiUrl"" TEXT NULL,
    ""VideoUrl"" TEXT NULL,
    ""Color"" TEXT NULL,
    ""DisplayWeight"" INTEGER NULL,
    ""Visibility"" TEXT NOT NULL,
    ""ReviewStatus"" TEXT NOT NULL,
    ""CreatedByUserId"" INTEGER NULL,
    ""LastEditedByUserId"" INTEGER NULL,
    ""DateAddedUtc"" TEXT NOT NULL,
    ""LastVerifiedUtc"" TEXT NULL
);";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CreateHighwayGroupsTableAsync(DbConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ""HighwayGroups"" (
    ""HighwayId"" INTEGER NOT NULL,
    ""GroupId"" INTEGER NOT NULL,
    ""Role"" TEXT NOT NULL DEFAULT 'Contributor',
    ""Evidence"" TEXT NULL,
    ""DateAddedUtc"" TEXT NOT NULL,
    CONSTRAINT ""PK_HighwayGroups"" PRIMARY KEY (""HighwayId"", ""GroupId""),
    CONSTRAINT ""FK_HighwayGroups_Highways_HighwayId"" FOREIGN KEY (""HighwayId"") REFERENCES ""Highways"" (""Id"") ON DELETE CASCADE,
    CONSTRAINT ""FK_HighwayGroups_Groups_GroupId"" FOREIGN KEY (""GroupId"") REFERENCES ""Groups"" (""Id"") ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS ""IX_HighwayGroups_GroupId"" ON ""HighwayGroups"" (""GroupId"");";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<HashSet<string>> GetColumnsAsync(DbConnection conn, string table)
    {
        var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{table}\");";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk
            cols.Add(reader.GetString(1));
        }
        return cols;
    }

    private async Task AddColumnIfMissingAsync(DbConnection conn, HashSet<string> existing, string table, string column, string definition)
    {
        if (existing.Contains(column)) return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
        await cmd.ExecuteNonQueryAsync();
        existing.Add(column);
        _logger.LogInformation("SchemaUpgrader: added column {Table}.{Column}", table, column);
    }

    private async Task RenameColumnIfNeededAsync(DbConnection conn, HashSet<string> existing, string table, string oldColumn, string newColumn)
    {
        if (existing.Contains(newColumn) || !existing.Contains(oldColumn)) return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER TABLE \"{table}\" RENAME COLUMN \"{oldColumn}\" TO \"{newColumn}\";";
        await cmd.ExecuteNonQueryAsync();
        existing.Remove(oldColumn);
        existing.Add(newColumn);
        _logger.LogInformation("SchemaUpgrader: renamed column {Table}.{Old} to {New}", table, oldColumn, newColumn);
    }

    private async Task MakeColumnNullableIfNeededAsync(DbConnection conn, string table, string column)
    {
        var isNotNull = false;
        {
            using var inspect = conn.CreateCommand();
            inspect.CommandText = $"PRAGMA table_info(\"{table}\");";
            using var reader = await inspect.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) continue;
                isNotNull = reader.GetInt32(3) != 0;
                break;
            }
        }

        if (!isNotNull) return;

        await using var transaction = await conn.BeginTransactionAsync();
        foreach (var sql in new[]
        {
            $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}Nullable\" TEXT NULL;",
            $"UPDATE \"{table}\" SET \"{column}Nullable\" = NULLIF(trim(\"{column}\"), '');",
            $"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\";",
            $"ALTER TABLE \"{table}\" RENAME COLUMN \"{column}Nullable\" TO \"{column}\";",
        })
        {
            using var command = conn.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        _logger.LogInformation("SchemaUpgrader: made column {Table}.{Column} nullable", table, column);
    }
}
