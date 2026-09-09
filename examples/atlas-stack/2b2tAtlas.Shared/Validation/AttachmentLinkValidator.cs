namespace Atlas.Validation;

/// <summary>Validates external location attachment links.</summary>
public static class AttachmentLinkValidator
{
    /// <summary>The maximum number of external links accepted for one location.</summary>
    public const int MaximumAttachmentsPerLocation = 50;

    /// <summary>Trims and canonicalizes an absolute HTTPS URL after rejecting credentials and values over 500 characters.</summary>
    /// <param name="value">The untrusted URL value.</param>
    /// <param name="normalized">Receives the absolute URI string when validation succeeds.</param>
    /// <param name="error">Receives a user-facing validation error when validation fails.</param>
    /// <returns><see langword="true"/> when the value is a valid external attachment URL; otherwise, <see langword="false"/>.</returns>
    public static bool TryNormalizeHttps(string? value, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Attachment URL is required.";
            return false;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            error = "Attachment URL must be an absolute HTTPS URL without embedded credentials.";
            return false;
        }

        normalized = uri.AbsoluteUri;
        if (normalized.Length > 500)
        {
            normalized = string.Empty;
            error = "Attachment URL cannot exceed 500 characters.";
            return false;
        }

        return true;
    }
}