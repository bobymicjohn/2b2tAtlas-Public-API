using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace _2b2tAtlas.Server.Services;

/// <summary>Private, append-only admission ledger and verified online SQLite snapshots.</summary>
public sealed class AtlasRecoveryStore(IConfiguration configuration)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Admission records also count failed attempts; restarting the API cannot reset limits.</summary>
    public sealed record Admission(DateTimeOffset Utc, int UserId, bool Owner, string Operation, string Snapshot, string Sha256);

    /// <summary>Limits each non-owner to 10/minute, 60/hour, 200/day; all non-owners share 120/hour and 400/day.</summary>
    public static bool WithinLimits(IEnumerable<Admission> records, int userId, DateTimeOffset now)
    {
        var recent = records.Where(r => !r.Owner && r.Utc > now.AddDays(-1)).ToArray();
        var own = recent.Where(r => r.UserId == userId).ToArray();
        return own.Count(r => r.Utc > now.AddMinutes(-1)) < 10 &&
            own.Count(r => r.Utc > now.AddHours(-1)) < 60 && own.Length < 200 &&
            recent.Count(r => r.Utc > now.AddHours(-1)) < 120 && recent.Length < 400;
    }

    /// <summary>Fails closed if the journal, disk or backup verification is unavailable.</summary>
    public async Task<bool> BeforeWriteAsync(int userId, bool owner, string operation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var root = configuration["Recovery:Root"] ?? @"B:\AtlasExample\Backups\human-edits";
            var source = configuration["Recovery:DatabasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "atlas.db");
            var now = DateTimeOffset.UtcNow;
            var records = new List<Admission>();
            Directory.CreateDirectory(root);
            foreach (var day in new[] { now.AddDays(-1), now })
            {
                var path = Path.Combine(root, day.ToString("yyyyMMdd"), "admissions.jsonl");
                if (File.Exists(path))
                    foreach (var line in File.ReadLines(path))
                        records.Add(JsonSerializer.Deserialize<Admission>(line) ?? throw new IOException("Invalid recovery journal."));
            }
            if (!owner && !WithinLimits(records, userId, now)) return false;
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).AvailableFreeSpace < 10L * 1024 * 1024 * 1024)
                throw new IOException("Recovery drive has less than 10 GiB free.");
            var folder = Path.Combine(root, now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(folder);
            var snapshot = Path.Combine(folder, $"{now:HHmmssfff}-{Guid.NewGuid():N}.db");
            using (var live = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
            using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString()))
            {
                live.Open(); backup.Open(); live.BackupDatabase(backup);
                using var check = backup.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
                if (!Equals(check.ExecuteScalar(), "ok")) throw new IOException("Recovery snapshot failed integrity check.");
            }
            using var stream = File.OpenRead(snapshot);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            var record = new Admission(now, userId, owner, operation, snapshot, hash);
            using var journal = new FileStream(Path.Combine(folder, "admissions.jsonl"), FileMode.Append,
                FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
            journal.Write(bytes); journal.Flush(flushToDisk: true);
            return true;
        }
        finally { gate.Release(); }
    }
}
