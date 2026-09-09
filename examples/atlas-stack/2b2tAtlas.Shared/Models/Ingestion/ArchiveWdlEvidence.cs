using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Atlas;

/// <summary>Bounded, reviewable provenance and identity evidence extracted from an Archive-style WDL.</summary>
/// <remarks>
/// Every value is untrusted metadata. It may improve matching and operator review, but it never proves
/// that terrain came from 2b2t and it never maps an unknown custom dimension to a vanilla dimension.
/// </remarks>
public sealed class ArchiveWdlEvidence
{
    /// <summary>Gets or sets the recognized downloader family.</summary>
    public string DownloaderKind { get; set; } = "unknown";
    /// <summary>Gets or sets the newest recognized machine-report schema version.</summary>
    public int? ReportSchemaVersion { get; set; }
    /// <summary>Gets or sets the number of valid completed machine-report sessions.</summary>
    public int ReportSessionCount { get; set; }
    /// <summary>Gets or sets the earliest reported capture start.</summary>
    public DateTime? StartedAtUtc { get; set; }
    /// <summary>Gets or sets the newest reported capture finish.</summary>
    public DateTime? FinishedAtUtc { get; set; }
    /// <summary>Gets or sets the latest reported finish state: complete, partial, or interrupted.</summary>
    public string? CompletionStatus { get; set; }
    /// <summary>Gets or sets whether the downloader left its unfinished-session sentinel in the save.</summary>
    public bool HasPendingCapture { get; set; }
    /// <summary>Gets or sets the downloader-supplied capture name.</summary>
    public string? DownloadName { get; set; }
    /// <summary>Gets or sets the captured server address.</summary>
    public string? SourceAddress { get; set; }
    /// <summary>Gets or sets the captured server-list name.</summary>
    public string? SourceName { get; set; }
    /// <summary>Gets or sets the captured server MOTD.</summary>
    public string? SourceMotd { get; set; }
    /// <summary>Gets or sets the downloader's source classification, such as multiplayer or replay.</summary>
    public string? SourceKind { get; set; }
    /// <summary>Gets or sets the server software brand recorded at capture time.</summary>
    public string? ServerBrand { get; set; }
    /// <summary>Gets or sets the Minecraft version used for the latest capture.</summary>
    public string? MinecraftVersion { get; set; }
    /// <summary>Gets or sets the Archive World Downloader version used for the latest capture.</summary>
    public string? ModVersion { get; set; }
    /// <summary>Gets or sets the mod loader name used for the latest capture.</summary>
    public string? LoaderName { get; set; }
    /// <summary>Gets or sets the mod loader version used for the latest capture.</summary>
    public string? LoaderVersion { get; set; }
    /// <summary>Gets or sets the downloader-reported, possibly normalized dimension.</summary>
    public string? ReportedDimension { get; set; }
    /// <summary>Gets or sets the raw dimension stored with player NBT.</summary>
    public string? PlayerDimension { get; set; }
    /// <summary>Gets or sets the downloaded-player X coordinate.</summary>
    public double? PlayerX { get; set; }
    /// <summary>Gets or sets the downloaded-player Y coordinate.</summary>
    public double? PlayerY { get; set; }
    /// <summary>Gets or sets the downloaded-player Z coordinate.</summary>
    public double? PlayerZ { get; set; }
    /// <summary>Gets or sets the latest session's received chunk count.</summary>
    public int? CapturedChunkCount { get; set; }
    /// <summary>Gets or sets the latest post-save on-disk chunk count.</summary>
    public int? SavedChunkCount { get; set; }
    /// <summary>Gets or sets the latest session's captured entity count.</summary>
    public int? EntityCount { get; set; }
    /// <summary>Gets or sets the latest session's captured container count.</summary>
    public int? ContainerCount { get; set; }
    /// <summary>Gets or sets whether bounded metadata identifies a known Archive address or name.</summary>
    public bool IsArchiveSource { get; set; }
    /// <summary>Gets or sets names eligible for reviewed location/warp matching.</summary>
    public List<string> NameCandidates { get; set; } = [];
    /// <summary>Gets or sets raw namespaced dimension identifiers retained by the capture.</summary>
    public List<string> RawDimensionIds { get; set; } = [];
    /// <summary>Gets or sets nonfatal metadata parsing warnings.</summary>
    public List<string> Warnings { get; set; } = [];

    /// <summary>Merges complementary evidence, preferring the richer worker-side value when supplied.</summary>
    public static ArchiveWdlEvidence? Merge(ArchiveWdlEvidence? intake, ArchiveWdlEvidence? inspected)
    {
        if (intake is null && inspected is null) return null;
        var first = inspected ?? intake!;
        var second = intake ?? inspected!;
        return new ArchiveWdlEvidence
        {
            DownloaderKind = first.DownloaderKind != "unknown" ? first.DownloaderKind : second.DownloaderKind,
            ReportSchemaVersion = first.ReportSchemaVersion ?? second.ReportSchemaVersion,
            ReportSessionCount = Math.Max(first.ReportSessionCount, second.ReportSessionCount),
            StartedAtUtc = Earliest(first.StartedAtUtc, second.StartedAtUtc),
            FinishedAtUtc = Latest(first.FinishedAtUtc, second.FinishedAtUtc),
            CompletionStatus = first.CompletionStatus ?? second.CompletionStatus,
            HasPendingCapture = first.HasPendingCapture || second.HasPendingCapture,
            DownloadName = first.DownloadName ?? second.DownloadName,
            SourceAddress = first.SourceAddress ?? second.SourceAddress,
            SourceName = first.SourceName ?? second.SourceName,
            SourceMotd = first.SourceMotd ?? second.SourceMotd,
            SourceKind = first.SourceKind ?? second.SourceKind,
            ServerBrand = first.ServerBrand ?? second.ServerBrand,
            MinecraftVersion = first.MinecraftVersion ?? second.MinecraftVersion,
            ModVersion = first.ModVersion ?? second.ModVersion,
            LoaderName = first.LoaderName ?? second.LoaderName,
            LoaderVersion = first.LoaderVersion ?? second.LoaderVersion,
            ReportedDimension = first.ReportedDimension ?? second.ReportedDimension,
            PlayerDimension = first.PlayerDimension ?? second.PlayerDimension,
            PlayerX = first.PlayerX ?? second.PlayerX,
            PlayerY = first.PlayerY ?? second.PlayerY,
            PlayerZ = first.PlayerZ ?? second.PlayerZ,
            CapturedChunkCount = first.CapturedChunkCount ?? second.CapturedChunkCount,
            SavedChunkCount = first.SavedChunkCount ?? second.SavedChunkCount,
            EntityCount = first.EntityCount ?? second.EntityCount,
            ContainerCount = first.ContainerCount ?? second.ContainerCount,
            IsArchiveSource = first.IsArchiveSource || second.IsArchiveSource,
            NameCandidates = DistinctBounded(first.NameCandidates.Concat(second.NameCandidates), 24),
            RawDimensionIds = DistinctBounded(first.RawDimensionIds.Concat(second.RawDimensionIds), 128),
            Warnings = DistinctBounded(first.Warnings.Concat(second.Warnings), 24),
        };
    }

    private static DateTime? Earliest(DateTime? first, DateTime? second) =>
        first is null ? second : second is null ? first : first < second ? first : second;

    private static DateTime? Latest(DateTime? first, DateTime? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    /// <summary>Normalizes an evidence collection to distinct, safe, bounded values.</summary>
    public static List<string> DistinctBounded(IEnumerable<string> values, int maximum) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Where(value => value.Length <= 240 && !value.Any(char.IsControl))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(maximum)
        .ToList();
}

/// <summary>Reads only small known metadata files from an untrusted WDL ZIP or extracted world.</summary>
public static partial class ArchiveWdlEvidenceReader
{
    private const long MaxMetadataBytes = 1_048_576;
    private const int MaxJsonLines = 256;

    /// <summary>Reads known bounded metadata entries directly from a ZIP central directory.</summary>
    public static ArchiveWdlEvidence ReadArchive(
        string zipPath,
        string? originalFileName = null,
        string? worldRoot = null)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var evidence = NewEvidence(originalFileName);
        var selectedRoot = worldRoot?.Replace('\\', '/').Trim('/');
        var metadataEntries = archive.Entries
            .Where(entry => IsKnownMetadata(entry.FullName.Replace('\\', '/')))
            .Where(entry => string.IsNullOrWhiteSpace(selectedRoot) ||
                entry.FullName.Replace('\\', '/').StartsWith(selectedRoot + "/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (metadataEntries.Count > 512)
            throw new InvalidDataException("Archive contains too many recognized WDL metadata entries.");
        var roots = metadataEntries
            .Select(entry => MetadataWorldRoot(entry.FullName.Replace('\\', '/')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (string.IsNullOrWhiteSpace(selectedRoot) && roots.Count > 1)
        {
            evidence.Warnings.Add("Metadata from multiple world roots was present; automatic Archive identity was disabled.");
            Finish(evidence);
            evidence.IsArchiveSource = false;
            return evidence;
        }
        var evidenceRoot = selectedRoot ?? (roots.Count == 1 ? roots[0] : null);
        if (evidenceRoot is not null)
            CollectNamespacedDimensionIds(archive, evidenceRoot, evidence);
        foreach (var entry in metadataEntries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.EndsWith("wdl/download.jsonl", StringComparison.OrdinalIgnoreCase))
                ParseDownloadReport(ReadEntry(entry), evidence);
            else if (normalized.EndsWith("wdl/download.pending", StringComparison.OrdinalIgnoreCase))
                ParsePendingReport(ReadEntry(entry), evidence);
            else if (normalized.EndsWith("WorldTools/Capture Metadata.md", StringComparison.OrdinalIgnoreCase))
                ParseWorldToolsMetadata(ReadEntry(entry), evidence);
            else if (normalized.EndsWith("WorldTools/Dimension Tree.txt", StringComparison.OrdinalIgnoreCase))
                ParseDimensionTree(ReadEntry(entry), evidence);
        }
        Finish(evidence);
        return evidence;
    }

    private static void CollectNamespacedDimensionIds(
        ZipArchive archive,
        string worldRoot,
        ArchiveWdlEvidence evidence)
    {
        var prefix = string.IsNullOrWhiteSpace(worldRoot) ? string.Empty : worldRoot.Trim('/') + "/";
        var dimensionsPrefix = prefix + "dimensions/";
        var found = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (!normalized.StartsWith(dimensionsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            var relative = normalized[dimensionsPrefix.Length..];
            var regionMarker = relative.IndexOf("/region/", StringComparison.OrdinalIgnoreCase);
            if (regionMarker <= 0 || !normalized.EndsWith(".mca", StringComparison.OrdinalIgnoreCase) &&
                !normalized.EndsWith(".mcr", StringComparison.OrdinalIgnoreCase)) continue;
            var dimensionPath = relative[..regionMarker];
            var separator = dimensionPath.IndexOf('/');
            if (separator <= 0 || separator == dimensionPath.Length - 1) continue;
            evidence.RawDimensionIds.Add(dimensionPath[..separator] + ":" + dimensionPath[(separator + 1)..]);
            if (++found >= 128) break;
        }
    }

    private static bool IsKnownMetadata(string path) =>
        path.EndsWith("wdl/download.jsonl", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("wdl/download.pending", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("WorldTools/Capture Metadata.md", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("WorldTools/Dimension Tree.txt", StringComparison.OrdinalIgnoreCase);

    private static string MetadataWorldRoot(string path)
    {
        foreach (var suffix in new[]
        {
            "wdl/download.jsonl", "wdl/download.pending", "WorldTools/Capture Metadata.md", "WorldTools/Dimension Tree.txt",
        })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return path[..^suffix.Length].TrimEnd('/');
        }
        return string.Empty;
    }

    /// <summary>Reads known bounded metadata files from an extracted world root.</summary>
    public static ArchiveWdlEvidence ReadWorldRoot(string rootPath)
    {
        var evidence = NewEvidence(null);
        ReadKnownFile(Path.Combine(rootPath, "wdl", "download.jsonl"), value => ParseDownloadReport(value, evidence));
        ReadKnownFile(Path.Combine(rootPath, "wdl", "download.pending"), value => ParsePendingReport(value, evidence));
        ReadKnownFile(Path.Combine(rootPath, "WorldTools", "Capture Metadata.md"), value => ParseWorldToolsMetadata(value, evidence));
        ReadKnownFile(Path.Combine(rootPath, "WorldTools", "Dimension Tree.txt"), value => ParseDimensionTree(value, evidence));
        Finish(evidence);
        return evidence;
    }

    private static ArchiveWdlEvidence NewEvidence(string? originalFileName)
    {
        var evidence = new ArchiveWdlEvidence();
        var stem = string.IsNullOrWhiteSpace(originalFileName) ? null : Path.GetFileNameWithoutExtension(originalFileName);
        if (!string.IsNullOrWhiteSpace(stem))
        {
            evidence.NameCandidates.Add(stem);
            var withoutWorldToolsTimestamp = EpochSuffixRegex().Replace(stem, string.Empty).Trim(' ', '-', '_');
            if (withoutWorldToolsTimestamp.Length > 0 && !withoutWorldToolsTimestamp.Equals(stem, StringComparison.Ordinal))
                evidence.NameCandidates.Add(withoutWorldToolsTimestamp);
        }
        return evidence;
    }

    private static void ParseDownloadReport(string content, ArchiveWdlEvidence evidence)
    {
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var downloadNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Take(MaxJsonLines))
        {
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 16 });
                var root = document.RootElement;
                evidence.DownloaderKind = "archive-world-downloader";
                evidence.ReportSessionCount++;
                evidence.ReportSchemaVersion = GetInt(root, "v") ?? evidence.ReportSchemaVersion;
                evidence.StartedAtUtc = Earliest(evidence.StartedAtUtc, GetInstant(root, "startedAt"));
                evidence.FinishedAtUtc = Latest(evidence.FinishedAtUtc, GetInstant(root, "finishedAt"));
                evidence.CompletionStatus = GetString(root, "status") ?? evidence.CompletionStatus;
                var downloadName = GetString(root, "downloadName");
                var sourceAddress = GetString(root, "sourceAddress");
                if (downloadName is not null) downloadNames.Add(downloadName);
                if (sourceAddress is not null) sourceAddresses.Add(sourceAddress);
                evidence.DownloadName = downloadName ?? evidence.DownloadName;
                evidence.SourceAddress = sourceAddress ?? evidence.SourceAddress;
                evidence.SourceName = GetString(root, "sourceName") ?? evidence.SourceName;
                evidence.SourceMotd = GetString(root, "sourceMotd") ?? evidence.SourceMotd;
                evidence.SourceKind = GetString(root, "sourceKind") ?? evidence.SourceKind;
                evidence.ServerBrand = GetString(root, "serverBrand") ?? evidence.ServerBrand;
                evidence.MinecraftVersion = GetString(root, "minecraftVersion") ?? evidence.MinecraftVersion;
                evidence.ModVersion = GetString(root, "modVersion") ?? evidence.ModVersion;
                evidence.LoaderName = GetString(root, "loaderName") ?? evidence.LoaderName;
                evidence.LoaderVersion = GetString(root, "loaderVersion") ?? evidence.LoaderVersion;
                evidence.ReportedDimension = GetString(root, "dimensionName") ?? evidence.ReportedDimension;
                evidence.CapturedChunkCount = GetInt(root, "chunks") ?? evidence.CapturedChunkCount;
                evidence.SavedChunkCount = GetInt(root, "saveChunks") ?? evidence.SavedChunkCount;
                evidence.EntityCount = GetInt(root, "entities") ?? evidence.EntityCount;
                evidence.ContainerCount = GetInt(root, "containers") ?? evidence.ContainerCount;
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name.StartsWith("d.", StringComparison.Ordinal) ||
                        property.Name.StartsWith("sd.", StringComparison.Ordinal))
                    {
                        var separator = property.Name.IndexOf('.');
                        if (separator >= 0 && separator < property.Name.Length - 1)
                            evidence.RawDimensionIds.Add(property.Name[(separator + 1)..]);
                    }
                }
            }
            catch (JsonException)
            {
                evidence.Warnings.Add("An Archive WDL report line was malformed and ignored.");
            }
        }
        if (downloadNames.Count > 1)
            evidence.Warnings.Add("The report contains sessions with different download names; identity needs review.");
        if (sourceAddresses.Count > 1)
            evidence.Warnings.Add("The report contains sessions from different server addresses; provenance needs review.");
        if (evidence.CompletionStatus?.Equals("partial", StringComparison.OrdinalIgnoreCase) == true)
            evidence.Warnings.Add("Archive World Downloader marked the latest capture partial; some data failed to save.");
        if (!string.IsNullOrWhiteSpace(evidence.DownloadName))
            evidence.NameCandidates.Add(evidence.DownloadName);
    }

    private static void ParsePendingReport(string content, ArchiveWdlEvidence evidence)
    {
        evidence.DownloaderKind = "archive-world-downloader";
        evidence.HasPendingCapture = true;
        evidence.CompletionStatus ??= "interrupted";
        evidence.Warnings.Add("An unfinished Archive World Downloader session marker is present; the capture may be interrupted.");
        var firstLine = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (firstLine is null) return;
        try
        {
            using var document = JsonDocument.Parse(firstLine, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            evidence.ReportSchemaVersion = GetInt(root, "v") ?? evidence.ReportSchemaVersion;
            evidence.StartedAtUtc = Earliest(evidence.StartedAtUtc, GetInstant(root, "startedAt"));
            evidence.DownloadName ??= GetString(root, "downloadName");
            evidence.SourceAddress ??= GetString(root, "sourceAddress");
            evidence.SourceName ??= GetString(root, "sourceName");
            evidence.SourceMotd ??= GetString(root, "sourceMotd");
            evidence.SourceKind ??= GetString(root, "sourceKind");
            evidence.ReportedDimension ??= GetString(root, "dimensionName");
        }
        catch (JsonException)
        {
            evidence.Warnings.Add("The unfinished Archive WDL session marker was malformed.");
        }
    }

    private static void ParseWorldToolsMetadata(string content, ArchiveWdlEvidence evidence)
    {
        evidence.DownloaderKind = evidence.DownloaderKind == "archive-world-downloader"
            ? evidence.DownloaderKind
            : "worldtools";
        foreach (var rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim().TrimStart('-', '*').Trim();
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            var key = Regex.Replace(line[..separator].ToLowerInvariant(), "[^a-z]", string.Empty);
            var value = line[(separator + 1)..].Trim().Trim('`');
            if (value.Length is 0 or > 500 || value.Any(char.IsControl)) continue;
            switch (key)
            {
                case "ip": case "address": case "serverip": evidence.SourceAddress ??= value; break;
                case "name": case "server": case "servername": evidence.SourceName ??= value; break;
                case "motd": evidence.SourceMotd ??= value; break;
            }
        }
    }

    private static void ParseDimensionTree(string content, ArchiveWdlEvidence evidence)
    {
        foreach (Match match in DimensionIdRegex().Matches(content).Take(128))
            evidence.RawDimensionIds.Add(match.Value);
    }

    private static void Finish(ArchiveWdlEvidence evidence)
    {
        if (!string.IsNullOrWhiteSpace(evidence.ReportedDimension))
            evidence.RawDimensionIds.Add(evidence.ReportedDimension);
        if (!string.IsNullOrWhiteSpace(evidence.PlayerDimension))
            evidence.RawDimensionIds.Add(evidence.PlayerDimension);
        evidence.NameCandidates = ArchiveWdlEvidence.DistinctBounded(evidence.NameCandidates, 24);
        evidence.RawDimensionIds = ArchiveWdlEvidence.DistinctBounded(evidence.RawDimensionIds, 128);
        evidence.Warnings = ArchiveWdlEvidence.DistinctBounded(evidence.Warnings, 24);
        evidence.IsArchiveSource = IsArchiveAddress(evidence.SourceAddress) ||
            ContainsArchiveName(evidence.SourceName) || ContainsArchiveName(evidence.SourceMotd);
    }

    private static bool IsArchiveAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = value.Contains("://", StringComparison.Ordinal) ? value : "minecraft://" + value;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.TrimEnd('.');
        return host.Equals("thearchive.world", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".thearchive.world", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("archive.spawnmason.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsArchiveName(string? value) =>
        value?.Contains("the archive", StringComparison.OrdinalIgnoreCase) == true;

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? Bounded(value.GetString(), 500)
            : null;

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) && parsed >= 0
            ? parsed
            : null;

    private static DateTime? GetInstant(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTime.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    private static DateTime? Earliest(DateTime? first, DateTime? second) =>
        first is null ? second : second is null ? first : first < second ? first : second;

    private static DateTime? Latest(DateTime? first, DateTime? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    private static string? Bounded(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl)
            ? value.Trim()
            : null;

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxMetadataBytes)
            throw new InvalidDataException($"WDL metadata file is larger than {MaxMetadataBytes:N0} bytes.");
        using var input = entry.Open();
        using var reader = new StreamReader(input, detectEncodingFromByteOrderMarks: true);
        var builder = new System.Text.StringBuilder((int)Math.Min(entry.Length, MaxMetadataBytes));
        var buffer = new char[8_192];
        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            if (builder.Length + read > MaxMetadataBytes)
                throw new InvalidDataException("WDL metadata expands beyond the safety limit.");
            builder.Append(buffer, 0, read);
        }
        return builder.ToString();
    }

    private static void ReadKnownFile(string path, Action<string> parse)
    {
        if (!File.Exists(path)) return;
        var info = new FileInfo(path);
        if (info.Length > MaxMetadataBytes)
            throw new InvalidDataException($"WDL metadata file is larger than {MaxMetadataBytes:N0} bytes.");
        parse(File.ReadAllText(path));
    }

    [GeneratedRegex("(?:[-_ ]+)(?:1[0-9]{10,13})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex EpochSuffixRegex();

    [GeneratedRegex("[a-z0-9_.-]+:[a-z0-9_./-]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DimensionIdRegex();
}
