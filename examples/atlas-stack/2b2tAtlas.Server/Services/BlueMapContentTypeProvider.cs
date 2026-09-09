using Microsoft.AspNetCore.StaticFiles;
using System.Text.RegularExpressions;

namespace _2b2tAtlas.Server.Services;

/// <summary>Serves BlueMap's public HOCON translations without exposing other configuration files.</summary>
public sealed partial class BlueMapContentTypeProvider : IContentTypeProvider
{
    private readonly FileExtensionContentTypeProvider _defaults = new();

    /// <inheritdoc />
    public bool TryGetContentType(string subpath, out string contentType)
    {
        if (Path.GetExtension(subpath).Equals(".conf", StringComparison.OrdinalIgnoreCase))
        {
            // BlueMap loads lang/settings.conf and lang/<locale>.conf at runtime.
            // ASP.NET's default provider rejects this extension with a 404.
            if (PublicLanguagePath().IsMatch(subpath.Replace('\\', '/')))
            {
                contentType = "text/plain; charset=utf-8";
                return true;
            }
            contentType = string.Empty;
            return false;
        }
        return _defaults.TryGetContentType(subpath, out contentType!);
    }

    [GeneratedRegex(@"(?:^|/)web/lang/[A-Za-z0-9_-]+\.conf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublicLanguagePath();
}
