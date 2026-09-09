using System.Security.Cryptography;

namespace Atlas;

/// <summary>
/// Stores immutable WDL ZIPs by SHA-256 under a content-addressed archive. The digest is the filename, so
/// duplicate submissions converge on one object and every re-render reads the exact bytes originally ingested.
/// </summary>
public static class WdlArchiveStore
{
    /// <summary>Returns the canonical object path for a lowercase or uppercase SHA-256 digest.</summary>
    /// <param name="root">Archive root.</param>
    /// <param name="sha256">Expected 64-character SHA-256 digest.</param>
    public static string ObjectPath(string root, string sha256)
    {
        var digest = NormalizeDigest(sha256);
        return Path.Combine(Path.GetFullPath(root), "objects", digest[..2], digest + ".zip");
    }

    /// <summary>Hashes and atomically archives a ZIP, returning its lowercase digest and canonical path.</summary>
    /// <param name="sourcePath">Existing source ZIP.</param>
    /// <param name="root">Archive root.</param>
    /// <param name="expectedSha256">Optional trusted digest that must match the source.</param>
    /// <param name="cancellationToken">Token that cancels hashing and copying.</param>
    public static async Task<(string Sha256, string Path, bool Created)> ArchiveAsync(
        string sourcePath,
        string root,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source)) throw new FileNotFoundException("WDL source archive was not found.", source);

        var digest = await ComputeSha256Async(source, cancellationToken);
        if (!string.IsNullOrWhiteSpace(expectedSha256) &&
            !digest.Equals(NormalizeDigest(expectedSha256), StringComparison.Ordinal))
            throw new InvalidDataException("WDL archive SHA-256 does not match the expected digest.");

        var destination = ObjectPath(root, digest);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            // When a resumed worker is already reading the canonical archive object, the source hash
            // above verified the destination itself. Avoid a second complete read across the NAS.
            if (source.Equals(Path.GetFullPath(destination),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                return (digest, destination, false);
            var existing = await ComputeSha256Async(destination, cancellationToken);
            if (!existing.Equals(digest, StringComparison.Ordinal))
                throw new InvalidDataException("Content-addressed WDL archive object has an invalid digest.");
            return (digest, destination, false);
        }

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1_048_576, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1_048_576, true))
                await input.CopyToAsync(output, cancellationToken);

            var copied = await ComputeSha256Async(temporary, cancellationToken);
            if (!copied.Equals(digest, StringComparison.Ordinal))
                throw new InvalidDataException("Archived WDL copy failed SHA-256 verification.");
            try { File.Move(temporary, destination); }
            catch (IOException) when (File.Exists(destination)) { File.Delete(temporary); }
            return (digest, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Copies a verified archived object to a local intake path when it is not already present.</summary>
    /// <param name="root">Archive root.</param>
    /// <param name="sha256">Expected archive digest.</param>
    /// <param name="destinationPath">Local destination path.</param>
    /// <param name="cancellationToken">Token that cancels verification and copying.</param>
    public static async Task MaterializeAsync(string root, string sha256, string destinationPath, CancellationToken cancellationToken)
    {
        var digest = NormalizeDigest(sha256);
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            if (string.Equals(await ComputeSha256Async(destination, cancellationToken), digest, StringComparison.Ordinal)) return;
            throw new InvalidDataException("Local WDL materialization path contains different bytes.");
        }

        var source = ObjectPath(root, digest);
        if (!File.Exists(source)) throw new FileNotFoundException("Archived WDL object is missing.", source);

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            // Read the landing zone once; authenticate the copied bytes on the
            // scratch SSD against the trusted content-addressed identity.
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1_048_576, true))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1_048_576, true))
                await input.CopyToAsync(output, cancellationToken);
            if (!string.Equals(await ComputeSha256Async(temporary, cancellationToken), digest, StringComparison.Ordinal))
                throw new InvalidDataException("Materialized WDL failed SHA-256 verification.");
            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Computes a lowercase SHA-256 digest for a file.</summary>
    /// <param name="path">File to hash.</param>
    /// <param name="cancellationToken">Token that cancels reading.</param>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1_048_576, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1_048_576];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string NormalizeDigest(string value)
    {
        var digest = value.Trim().ToLowerInvariant();
        if (digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("SHA-256 digest must contain exactly 64 hexadecimal characters.", nameof(value));
        return digest;
    }
}
