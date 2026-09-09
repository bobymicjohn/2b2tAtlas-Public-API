using System.Text.Json;
using Atlas;
using Atlas.Enrichment;
using Microsoft.Extensions.Options;
using ServerGroup = _2b2tAtlas.Server.Models.Group;

namespace _2b2tAtlas.Server.Services.AiEnrichment;

/// <summary>A lightweight status snapshot for the revision-pinned group evidence index.</summary>
public readonly record struct GroupEvidenceIndexStatus(
    bool Available,
    DateTime? GeneratedUtc,
    int ArticleCount,
    string? Message);

/// <summary>An explicit group/location pair present in the evidence index.</summary>
public readonly record struct GroupEvidenceCandidate(int LocationId, int GroupId);

/// <summary>
/// Reads the deterministic output of <c>audit-2b2t-wiki-groups.py</c> and turns only explicit base/build
/// evidence into reviewable Atlas group attributions. The index deliberately excludes incidental article
/// links, proximity, render overlap, residents, visitors, griefers, allies, and missing-number name guesses.
/// </summary>
public sealed class GroupEvidenceIndexService
{
    private readonly AiEnrichmentOptions _options;
    private readonly string _indexPath;
    private readonly ILogger<GroupEvidenceIndexService> _logger;
    private readonly object _gate = new();
    private DateTime _cachedWriteUtc;
    private GroupEvidenceIndex? _cached;
    private string? _cachedError;

    /// <summary>Initializes the evidence reader.</summary>
    public GroupEvidenceIndexService(
        IOptions<AiEnrichmentOptions> options,
        IHostEnvironment environment,
        ILogger<GroupEvidenceIndexService> logger)
    {
        _options = options.Value;
        _logger = logger;
        _indexPath = Path.IsPathRooted(_options.GroupEvidenceIndexPath)
            ? Path.GetFullPath(_options.GroupEvidenceIndexPath)
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, _options.GroupEvidenceIndexPath));
    }

    /// <summary>Returns index availability, freshness, and coverage without exposing source text.</summary>
    public GroupEvidenceIndexStatus GetStatus()
    {
        var index = Load();
        return index is null
            ? new GroupEvidenceIndexStatus(false, null, 0, _cachedError ?? "Group evidence index is unavailable.")
            : new GroupEvidenceIndexStatus(true, index.GeneratedUtc, index.Articles.Count,
                $"Revision-pinned group evidence loaded from {Path.GetFileName(_indexPath)}.");
    }

    /// <summary>
    /// Finds additive group/build relationships for one location. Exact database row identifiers from the
    /// freshly regenerated audit are required, which makes duplicate location names fail closed. Existing
    /// reviewed links are omitted by default, or may be returned as read-only drafting context.
    /// </summary>
    /// <param name="location">The Atlas location and its current reviewed relationships.</param>
    /// <param name="knownGroups">The current Atlas group catalog used to resolve stable identities.</param>
    /// <param name="includeExisting">Whether to include already-linked groups as pinned model context.</param>
    /// <returns>Revision-pinned suggestions ordered by confidence.</returns>
    public IReadOnlyList<GroupAttributionSuggestion> FindMatches(
        Atlas.Location location,
        IReadOnlyCollection<ServerGroup> knownGroups,
        bool includeExisting = false)
    {
        var index = Load();
        if (index is null)
            return [];

        var existingIds = (location.Groups ?? []).Select(item => item.GroupId).ToHashSet();
        var groupsById = knownGroups.ToDictionary(group => group.Id);
        var groupsByName = knownGroups
            .GroupBy(group => Normalize(group.Name))
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var suggestions = new Dictionary<int, GroupAttributionSuggestion>();

        foreach (var article in index.Articles)
        {
            if (article.AtlasGroups.Count != 1)
                continue;

            var indexedGroup = article.AtlasGroups[0];
            ServerGroup? group = null;
            if (indexedGroup.Id > 0)
                groupsById.TryGetValue(indexedGroup.Id, out group);
            if (group is null && !string.IsNullOrWhiteSpace(indexedGroup.Name))
                groupsByName.TryGetValue(Normalize(indexedGroup.Name), out group);
            if (group is null || (!includeExisting && existingIds.Contains(group.Id)))
                continue;

            foreach (var match in article.ExactAtlasBuildMatches.Where(match => match.Rowid == location.Rowid))
            {
                var evidence = match.Evidence.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var infobox = evidence.Contains("Infobox group bases", StringComparer.OrdinalIgnoreCase);
                var titleIdentity = evidence.Contains("building-group title equals location", StringComparer.OrdinalIgnoreCase);
                var section = evidence.Contains("base/build section", StringComparer.OrdinalIgnoreCase);
                if (!infobox && !titleIdentity && !section)
                    continue;

                var iterationEquivalent = evidence.Contains(
                    "trailing Roman/Arabic iteration equivalence", StringComparer.OrdinalIgnoreCase);
                var confidence = infobox ? 0.97 : titleIdentity ? 0.88 : 0.84;
                if (iterationEquivalent)
                    confidence -= 0.03;
                // A group article whose title equals a location can still describe the organization rather
                // than an owned build (reviewed examples: The Last Templar and Wingston). Only an explicit
                // infobox base declaration clears the automatic gate.
                var autoApply = infobox && confidence >= 0.93;
                var candidate = new GroupAttributionSuggestion
                {
                    GroupId = group.Id,
                    GroupName = group.Name,
                    Role = "Builder",
                    EvidenceUrl = article.PublicUrl,
                    SourceRevisionId = article.RevisionId,
                    Evidence = string.Join(", ", evidence),
                    Confidence = confidence,
                    AutoApplyEligible = autoApply,
                };

                if (!suggestions.TryGetValue(group.Id, out var prior) || candidate.Confidence > prior.Confidence)
                    suggestions[group.Id] = candidate;
            }
        }

        return suggestions.Values.OrderByDescending(item => item.Confidence).ThenBy(item => item.GroupName).ToArray();
    }

    /// <summary>Returns explicit group/location pairs so batch runs can skip already-linked rows without starvation.</summary>
    public IReadOnlySet<GroupEvidenceCandidate> GetCandidatePairs(IReadOnlyCollection<ServerGroup> knownGroups)
    {
        var index = Load();
        if (index is null)
            return new HashSet<GroupEvidenceCandidate>();

        var groupsById = knownGroups.ToDictionary(group => group.Id);
        var groupsByName = knownGroups
            .GroupBy(group => Normalize(group.Name))
            .Where(group => group.Key.Length > 0 && group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var pairs = new HashSet<GroupEvidenceCandidate>();
        foreach (var article in index.Articles.Where(article => article.AtlasGroups.Count == 1))
        {
            var indexedGroup = article.AtlasGroups[0];
            ServerGroup? group = null;
            if (indexedGroup.Id > 0)
                groupsById.TryGetValue(indexedGroup.Id, out group);
            if (group is null && !string.IsNullOrWhiteSpace(indexedGroup.Name))
                groupsByName.TryGetValue(Normalize(indexedGroup.Name), out group);
            if (group is null)
                continue;

            foreach (var match in article.ExactAtlasBuildMatches)
            {
                if (match.Rowid <= 0 || !HasExplicitEvidence(match.Evidence))
                    continue;
                pairs.Add(new GroupEvidenceCandidate(match.Rowid, group.Id));
            }
        }
        return pairs;
    }

    /// <summary>
    /// Returns review-only candidates for wiki group articles that are not represented in Atlas but name at
    /// least one existing Atlas build through explicit evidence. No group is created by this method.
    /// </summary>
    public IReadOnlyList<GroupDiscoverySuggestion> GetGroupDiscoveryCandidates(
        IReadOnlyCollection<ServerGroup> knownGroups)
    {
        var index = Load();
        if (index is null)
            return [];

        var knownNames = knownGroups.Select(group => Normalize(group.Name)).ToHashSet();
        var denied = _options.GroupDiscoveryDenyList.Select(Normalize).ToHashSet();
        return index.Articles
            .Where(article => article.AtlasGroups.Count == 0)
            .Where(article => !knownNames.Contains(Normalize(article.Name)) &&
                              !knownNames.Contains(Normalize(article.Title)))
            .Where(article => !denied.Contains(Normalize(article.Name)) &&
                              !denied.Contains(Normalize(article.Title)))
            .Select(article => new GroupDiscoverySuggestion
            {
                PageId = article.PageId,
                Name = string.IsNullOrWhiteSpace(article.Name) ? article.Title : article.Name,
                Type = Enum.TryParse<GroupType>(article.Type, true, out var type) ? type : GroupType.Other,
                WikiUrl = article.PublicUrl,
                SourceRevisionId = article.RevisionId,
                Intro = article.Intro,
                Founded = article.Founded,
                Status = article.Status,
                LogoUrl = article.LogoUrl,
                LogoSourceUrl = article.LogoSourceUrl,
                WebsiteUrl = article.WebsiteCandidate,
                DiscordUrl = article.DiscordCandidate,
                Locations = article.ExactAtlasBuildMatches
                    .Where(match => match.Rowid > 0 && HasExplicitEvidence(match.Evidence))
                    .GroupBy(match => match.Rowid)
                    .Select(group => group.First())
                    .Select(match => new GroupDiscoveryLocation
                    {
                        LocationId = match.Rowid,
                        LocationName = match.Name,
                        Role = "Builder",
                        Evidence = string.Join(", ", match.Evidence),
                    }).ToList(),
            })
            .Where(candidate => candidate.PageId > 0 && candidate.Locations.Count > 0)
            .OrderByDescending(candidate => candidate.Locations.Count)
            .ThenBy(candidate => candidate.Name)
            .ToArray();
    }

    private GroupEvidenceIndex? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_indexPath))
            {
                _cached = null;
                _cachedError = $"Run the group evidence refresh; index not found at {_indexPath}.";
                return null;
            }

            var writeUtc = File.GetLastWriteTimeUtc(_indexPath);
            if (_cached is not null && writeUtc == _cachedWriteUtc)
                return IsFresh(_cached) ? _cached : null;
            if (_cached is null && writeUtc == _cachedWriteUtc && _cachedError is not null)
                return null;

            try
            {
                using var stream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var index = JsonSerializer.Deserialize<GroupEvidenceIndex>(stream, JsonOptions);
                if (index is null || index.SchemaVersion != 1 || index.Articles.Count == 0)
                    throw new InvalidDataException("The group evidence index is empty or has an unsupported schema.");
                _cachedWriteUtc = writeUtc;
                _cached = index;
                _cachedError = null;
                if (!IsFresh(index))
                    return null;
                return index;
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException)
            {
                _cachedWriteUtc = writeUtc;
                _cached = null;
                _cachedError = $"Group evidence index could not be loaded: {exception.Message}";
                _logger.LogWarning(exception, "Could not load group evidence index {IndexPath}.", _indexPath);
                return null;
            }
        }
    }

    private bool IsFresh(GroupEvidenceIndex index)
    {
        var maxAge = TimeSpan.FromDays(Math.Clamp(_options.GroupEvidenceMaxAgeDays, 1, 90));
        if (DateTime.UtcNow - index.GeneratedUtc <= maxAge)
            return true;
        _cachedError = $"Group evidence index is stale ({index.GeneratedUtc:O}); refresh it before applying attributions.";
        return false;
    }

    private static string Normalize(string? value) => new((value ?? string.Empty)
        .ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool HasExplicitEvidence(IEnumerable<string> evidence) => evidence.Any(item =>
        item.Equals("Infobox group bases", StringComparison.OrdinalIgnoreCase) ||
        item.Equals("building-group title equals location", StringComparison.OrdinalIgnoreCase) ||
        item.Equals("base/build section", StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class GroupEvidenceIndex
    {
        public int SchemaVersion { get; set; }
        public DateTime GeneratedUtc { get; set; }
        public List<GroupEvidenceArticle> Articles { get; set; } = [];
    }

    private sealed class GroupEvidenceArticle
    {
        public int PageId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = "Other";
        public string PublicUrl { get; set; } = string.Empty;
        public long? RevisionId { get; set; }
        public string? Intro { get; set; }
        public string? Founded { get; set; }
        public string? Status { get; set; }
        public string? LogoUrl { get; set; }
        public string? LogoSourceUrl { get; set; }
        public string? WebsiteCandidate { get; set; }
        public string? DiscordCandidate { get; set; }
        public List<IndexedGroup> AtlasGroups { get; set; } = [];
        public List<IndexedLocationMatch> ExactAtlasBuildMatches { get; set; } = [];
    }

    private sealed class IndexedGroup
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class IndexedLocationMatch
    {
        public int Rowid { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<string> Evidence { get; set; } = [];
    }
}
