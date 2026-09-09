using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace _2b2tAtlas.Client.Services;

/// <summary>Small, bundled directory of verified channel identities and avatars.</summary>
public static partial class YoutubeCreators
{
    private static readonly Lazy<List<YoutubeCreator>> Profiles = new(() =>
    {
        var assembly = typeof(YoutubeCreators).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".Data.youtube-creators.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource);
        return stream is null ? [] : JsonSerializer.Deserialize(stream, CreatorJsonContext.Default.ListYoutubeCreator) ?? [];
    });

    /// <summary>Prefer verified video ownership, then an exact known channel name.</summary>
    public static YoutubeCreator? Find(string videoId, string? attribution) =>
        Profiles.Value.FirstOrDefault(p => p.VideoIds.Contains(videoId, StringComparer.Ordinal)) ??
        Profiles.Value.FirstOrDefault(p => string.Equals(p.Name, attribution?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Extract a case-sensitive video identity from a supported YouTube URL.</summary>
    public static string? VideoId(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.Host.ToLowerInvariant();
        string? id = null;
        if (host is "youtu.be" or "www.youtu.be") id = uri.AbsolutePath.Trim('/');
        else if (host is "youtube.com" or "www.youtube.com" or "m.youtube.com" or "music.youtube.com" or "www.youtube-nocookie.com" or "youtube-nocookie.com")
        {
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] is "shorts" or "embed" or "live") id = parts[1];
            else if (uri.AbsolutePath == "/watch") id = QueryValue(uri, "v");
        }
        return id is not null && VideoIdPattern().IsMatch(id) ? id : null;
    }

    /// <summary>Format a linked chapter's start time without implying video duration.</summary>
    public static string? StartTime(string? value)
    {
        if (VideoId(value) is null || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        var raw = QueryValue(uri, "t") ?? QueryValue(uri, "start");
        if (raw is null) return null;
        long seconds;
        if (!long.TryParse(raw, out seconds))
        {
            var match = TimePattern().Match(raw);
            if (!match.Success) return null;
            seconds = 0;
            for (var i = 1; i <= 3; i++)
                if (long.TryParse(match.Groups[i].Value, out var part)) seconds += part * (i == 1 ? 3600 : i == 2 ? 60 : 1);
        }
        if (seconds <= 0 || seconds > 604800) return null;
        return seconds >= 3600 ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}" : $"{seconds / 60}:{seconds % 60:00}";
    }

    private static string? QueryValue(Uri uri, string key) => uri.Query.TrimStart('?').Split('&')
        .Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] == key)
        .Select(p => Uri.UnescapeDataString(p[1])).FirstOrDefault();

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex VideoIdPattern();
    [GeneratedRegex("^(?:(\\d{1,6})h)?(?:(\\d{1,6})m)?(?:(\\d{1,6})s)?$")]
    private static partial Regex TimePattern();
}

/// <summary>Public channel identity and a locally cached profile image.</summary>
public sealed class YoutubeCreator
{
    /// <summary>Display name returned by the channel.</summary>
    public string Name { get; set; } = "";
    /// <summary>Canonical channel URL.</summary>
    public string ChannelUrl { get; set; } = "";
    /// <summary>Site-relative avatar asset path.</summary>
    public string AvatarPath { get; set; } = "";
    /// <summary>Video IDs whose metadata identifies this channel.</summary>
    public List<string> VideoIds { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<YoutubeCreator>))]
internal partial class CreatorJsonContext : JsonSerializerContext;
