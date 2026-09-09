using System.ComponentModel.DataAnnotations;

namespace Atlas.Validation;

/// <summary>Allows an empty value or an absolute HTTP/HTTPS URL.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class OptionalHttpUrlAttribute : ValidationAttribute
{
    /// <summary>Determines whether a value is empty or an absolute HTTP/HTTPS URL without embedded credentials.</summary>
    /// <param name="value">The value to validate.</param>
    /// <returns><see langword="true"/> for null, blank, or valid HTTP/HTTPS URL strings; otherwise, <see langword="false"/>.</returns>
    public override bool IsValid(object? value)
    {
        if (value is null) return true;
        if (value is not string text) return false;
        if (string.IsNullOrWhiteSpace(text)) return true;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               !string.IsNullOrWhiteSpace(uri.Host) &&
               string.IsNullOrEmpty(uri.UserInfo);
    }
}
