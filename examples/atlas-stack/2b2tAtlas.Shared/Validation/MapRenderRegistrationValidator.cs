using System.Globalization;
using System.Text.RegularExpressions;

namespace Atlas;

/// <summary>Validates untrusted dimension-level map render registrations against the public wire contract.</summary>
public static partial class MapRenderRegistrationValidator
{
    /// <summary>Validates metadata, dimension mapping, date, zoom, placeholders, and the approved tile URL root.</summary>
    /// <param name="dto">The render registration to validate.</param>
    /// <param name="allowedUrlPrefixes">Approved absolute HTTPS or same-origin tile path prefixes.</param>
    /// <returns>A list of validation errors; an empty list indicates a valid registration.</returns>
    public static IReadOnlyList<string> Validate(MapRenderDto dto, IReadOnlyCollection<string> allowedUrlPrefixes)
    {
        var errors = new List<string>();
        var slug = dto.Slug?.Trim() ?? string.Empty;
        var name = dto.Name?.Trim() ?? string.Empty;
        var scale = dto.Scale?.Trim() ?? string.Empty;
        var source = dto.Source?.Trim() ?? string.Empty;
        var template = dto.UrlTemplate?.Trim() ?? string.Empty;

        if (!SlugPattern().IsMatch(slug))
            errors.Add("Slug must be 1-64 lowercase letters, numbers, or hyphens.");
        if (!ValidText(name, 100))
            errors.Add("Name must be 1-100 characters without control characters.");
        if (!ScalePattern().IsMatch(scale))
            errors.Add("Scale must look like 5k, 256k, or 1m.");
        if (!ValidText(source, 200))
            errors.Add("Source must be 1-200 characters without control characters.");
        if (dto.Dimension is < 0 or > 2)
            errors.Add("Dimension must be 0, 1, or 2.");
        if (dto.MaxNativeZoom is < 0 or > 30)
            errors.Add("MaxNativeZoom must be between 0 and 30.");
        if (dto.SortOrder is < -10_000 or > 10_000)
            errors.Add("SortOrder must be between -10000 and 10000.");

        if (!DateOnly.TryParseExact(
                dto.WorldDownloadDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var worldDate) ||
            worldDate > DateOnly.FromDateTime(DateTime.UtcNow))
        {
            errors.Add("WorldDownloadDate must be a non-future date in YYYY-MM-DD format.");
        }

        ValidateTemplate(template, dto.HasDayNight, allowedUrlPrefixes, errors);
        return errors;
    }

    private static void ValidateTemplate(
        string template,
        bool hasDayNight,
        IReadOnlyCollection<string> allowedPrefixes,
        ICollection<string> errors)
    {
        if (template.Length is < 1 or > 500 || template.Any(char.IsControl) || template.Contains('\\'))
        {
            errors.Add("UrlTemplate must be 1-500 characters without control characters or backslashes.");
            return;
        }

        foreach (var token in new[] { "{z}", "{y}", "{x}" })
        {
            if (Count(template, token) != 1)
                errors.Add($"UrlTemplate must contain exactly one {token} token.");
        }
        if (hasDayNight != (Count(template, "{dn}") == 1))
            errors.Add("UrlTemplate must contain exactly one {dn} token if and only if HasDayNight is true.");

        var allowedTokens = new HashSet<string>(StringComparer.Ordinal) { "{z}", "{y}", "{x}", "{dn}" };
        if (PlaceholderPattern().Matches(template).Select(match => match.Value).Any(token => !allowedTokens.Contains(token)))
            errors.Add("UrlTemplate contains an unknown placeholder.");

        var concrete = template
            .Replace("{z}", "1", StringComparison.Ordinal)
            .Replace("{y}", "0", StringComparison.Ordinal)
            .Replace("{x}", "0", StringComparison.Ordinal)
            .Replace("{dn}", "day", StringComparison.Ordinal);
        if (!IsAllowedUrl(concrete, allowedPrefixes))
            errors.Add("UrlTemplate must be under an approved HTTPS or same-origin tile prefix.");
        var withoutTokens = template;
        foreach (var token in allowedTokens)
            withoutTokens = withoutTokens.Replace(token, string.Empty, StringComparison.Ordinal);
        if (withoutTokens.Contains('{') || withoutTokens.Contains('}'))
            errors.Add("UrlTemplate contains unmatched or nested braces.");
    }

    private static bool IsAllowedUrl(string candidate, IReadOnlyCollection<string> allowedPrefixes)
    {
        if (candidate.Contains('?') || candidate.Contains('#') || allowedPrefixes.Count == 0)
            return false;

        if (candidate.StartsWith('/') && !candidate.StartsWith("//", StringComparison.Ordinal))
        {
            return allowedPrefixes.Any(prefix =>
                prefix.StartsWith('/') &&
                !prefix.StartsWith("//", StringComparison.Ordinal) &&
                candidate.StartsWith(EnsureTrailingSlash(prefix), StringComparison.Ordinal));
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        foreach (var prefix in allowedPrefixes)
        {
            if (!Uri.TryCreate(EnsureTrailingSlash(prefix), UriKind.Absolute, out var allowed) ||
                allowed.Scheme != Uri.UriSchemeHttps)
            {
                continue;
            }
            if (uri.Scheme == allowed.Scheme &&
                uri.IdnHost.Equals(allowed.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                uri.Port == allowed.Port &&
                uri.AbsolutePath.StartsWith(allowed.AbsolutePath, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ValidText(string value, int maxLength) =>
        value.Length is > 0 && value.Length <= maxLength && !value.Any(char.IsControl);

    private static int Count(string value, string token) =>
        value.Split(token, StringSplitOptions.None).Length - 1;

    private static string EnsureTrailingSlash(string value) =>
        value.EndsWith('/') ? value : value + "/";

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[1-9][0-9]*(?:\\.[0-9]+)?[km]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ScalePattern();

    [GeneratedRegex("\\{[^{}]+\\}", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PlaceholderPattern();
}
