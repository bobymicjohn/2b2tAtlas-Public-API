namespace _2b2tAtlas.Server.Services;

/// <summary>Configures the read-only Archive collector status surface.</summary>
public sealed class ArchiveCollectorStatusOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ArchiveCollectorStatus";

    /// <summary>Collector run root containing queue and status checkpoints.</summary>
    public string RunRoot { get; set; } = @"C:\AtlasExample\Ingest\archive-sync\example-catalog";
    /// <summary>Root of the primary collector profile.</summary>
    public string PrimaryProfileRoot { get; set; } = @"C:\AtlasExample\Ingest\archive-sync\collector";
    /// <summary>Parent directory of additional isolated collector profiles.</summary>
    public string ProfilesRoot { get; set; } = @"C:\AtlasExample\Ingest\archive-sync\collectors";
    /// <summary>Isolated worker-five profile used for Minecraft 1.21.10 compatibility retries.</summary>
    public string CompatibilityProfileRoot { get; set; } = @"D:\AtlasExample\Ingest\archive-compat\collector-1.21.10";
    /// <summary>Operator/disk safety latch, independent of supervisor heartbeat.</summary>
    public string PauseSignalPath { get; set; } = @"C:\AtlasExample\Ingest\pause-collector";
    /// <summary>Server-side snapshot cache lifetime in seconds.</summary>
    public int CacheSeconds { get; set; } = 10;
    /// <summary>Age in seconds after which a supervisor heartbeat is stale.</summary>
    public int StaleAfterSeconds { get; set; } = 90;
    /// <summary>Configured weekday for the recurring catalog check.</summary>
    public DayOfWeek WeeklyRunDay { get; set; } = DayOfWeek.Sunday;
    /// <summary>Configured local hour for the recurring catalog check.</summary>
    public int WeeklyRunHour { get; set; } = 6;
}
