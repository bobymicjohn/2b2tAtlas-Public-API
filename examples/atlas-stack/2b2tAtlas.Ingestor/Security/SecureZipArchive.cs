using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Models;

namespace Atlas.Ingestor.Security;

/// <summary>Performs bounded, fail-closed inspection and extraction of untrusted Minecraft ZIP archives.</summary>
/// <remarks>
/// Inspection validates ZIP/ZIP64 central-directory bounds, entry methods and types, normalized paths, collisions,
/// declared sizes, compression ratios, and archive identity. These checks establish structural safety only; they do
/// not prove that world data came from 2b2t or from the source named by an operator.
/// </remarks>
public static partial class SecureZipArchive
{
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixRegularFile = 0x8000;
    private const int UnixDirectory = 0x4000;
    private const int UnixSymbolicLink = 0xA000;

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "clock$",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>Inspects an archive without extracting it or executing archive content.</summary>
    /// <param name="archivePath">Path to a local ZIP archive.</param>
    /// <param name="limits">Archive, entry, expansion, ratio, and path limits.</param>
    /// <param name="expectedSha256">Optional digest that the current archive bytes must match.</param>
    /// <param name="cancellationToken">Token checked during hashing and entry traversal.</param>
    /// <returns>A report binding archive identity to validated metadata and candidate world markers.</returns>
    /// <exception cref="InputValidationException">The file is absent, not ZIP, corrupt, unsupported, or has neither level metadata nor recognized chunk storage.</exception>
    /// <exception cref="InputSecurityException">Identity, entry type, path, collision, count, size, or ratio checks fail.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>No output files or directories are created.</remarks>
    public static async Task<ArchiveReport> InspectAsync(
        string archivePath,
        IngestLimits limits,
        string? expectedSha256 = null,
        CancellationToken cancellationToken = default)
    {
        limits.Validate();
        var file = new FileInfo(Path.GetFullPath(archivePath));
        if (!file.Exists)
            throw new InputValidationException($"Archive does not exist: {file.FullName}");
        if (!file.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InputValidationException("Only ZIP archives are accepted. Convert other formats offline first.");
        if (file.Length > limits.MaxArchiveBytes)
            throw new InputSecurityException($"Archive is {file.Length:N0} bytes; limit is {limits.MaxArchiveBytes:N0}.");

        var sha256 = await ComputeSha256Async(file.FullName, limits.MaxArchiveBytes, cancellationToken);
        if (expectedSha256 is not null && !sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InputSecurityException("Archive changed after the immutable snapshot was created.");

        ZipCentralDirectory.Validate(file.FullName, limits.MaxEntries);

        long expandedBytes = 0;
        var files = 0;
        var directories = 0;
        var levelDatCandidates = new List<string>();
        var hasChunkStorage = false;
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var archive = ZipFile.OpenRead(file.FullName);
            if (archive.Entries.Count > limits.MaxEntries)
                throw new InputSecurityException($"Archive has {archive.Entries.Count:N0} entries; limit is {limits.MaxEntries:N0}.");

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = ValidateEntryPath(entry.FullName, limits);
                if (!names.TryAdd(normalized, entry.FullName))
                    throw new InputSecurityException($"Archive has duplicate or case-colliding path: {entry.FullName}");

                ValidateEntryType(entry);
                if (IsDirectory(entry))
                {
                    directories++;
                    continue;
                }

                files++;
                if (entry.Length > limits.MaxSingleEntryBytes)
                    throw new InputSecurityException($"Entry exceeds the single-file limit: {entry.FullName}");
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > limits.MaxExpandedBytes)
                    throw new InputSecurityException($"Archive expands beyond {limits.MaxExpandedBytes:N0} bytes.");

                var ratio = entry.Length / (double)Math.Max(1, entry.CompressedLength);
                if (entry.Length >= IngestLimits.MiB && ratio > limits.MaxCompressionRatio)
                    throw new InputSecurityException($"Entry compression ratio {ratio:N1}:1 is unsafe: {entry.FullName}");

                if (normalized.Equals("level.dat", StringComparison.OrdinalIgnoreCase) ||
                    normalized.EndsWith("/level.dat", StringComparison.OrdinalIgnoreCase))
                {
                    levelDatCandidates.Add(normalized);
                }
                if (MinecraftStoragePath().IsMatch(normalized))
                    hasChunkStorage = true;
            }

            if (levelDatCandidates.Count == 0 && !hasChunkStorage)
                throw new InputValidationException(
                    "Archive contains neither level.dat nor recognized Minecraft Java chunk storage.");

            return new ArchiveReport(
                file.FullName,
                sha256,
                file.Length,
                expandedBytes,
                archive.Entries.Count,
                files,
                directories,
                levelDatCandidates);
        }
        catch (InvalidDataException exception)
        {
            throw new InputValidationException("ZIP archive is corrupt, encrypted, or unsupported.", exception);
        }
        catch (OverflowException exception)
        {
            throw new InputSecurityException("Archive expanded-size accounting overflowed.", exception);
        }
    }

    [GeneratedRegex(@"(?:^|/)(?:region/r\.-?[0-9]+\.-?[0-9]+\.(?:mca|mcr)|c\.-?[0-9a-z]+\.-?[0-9a-z]+\.dat)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex MinecraftStoragePath();

    /// <summary>Re-inspects an immutable snapshot and extracts it into a newly created destination.</summary>
    /// <param name="snapshotPath">Path to the job's immutable ZIP snapshot.</param>
    /// <param name="destinationPath">Final extraction directory, which must not already exist.</param>
    /// <param name="report">Previously persisted inspection report.</param>
    /// <param name="limits">Archive and extraction safety limits.</param>
    /// <param name="cancellationToken">Token checked before each entry and during bounded copies.</param>
    /// <returns>A task that completes after a verified temporary tree is atomically renamed to the destination.</returns>
    /// <exception cref="InputValidationException">The destination is invalid, free space is insufficient, or the archive is malformed.</exception>
    /// <exception cref="InputSecurityException">Snapshot identity or metadata changed, a path/type check fails, or extracted bytes exceed declarations or limits.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>Any partial extraction tree is recursively deleted on cancellation or failure.</remarks>
    public static async Task ExtractAsync(
        string snapshotPath,
        string destinationPath,
        ArchiveReport report,
        IngestLimits limits,
        CancellationToken cancellationToken = default)
    {
        var currentReport = await InspectAsync(snapshotPath, limits, report.Sha256, cancellationToken);
        if (currentReport.EntryCount != report.EntryCount || currentReport.ExpandedBytes != report.ExpandedBytes)
            throw new InputSecurityException("Archive metadata changed after preflight.");

        var destination = Path.GetFullPath(destinationPath);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new InputValidationException($"Extraction destination already exists: {destination}");

        var parent = Directory.GetParent(destination)
            ?? throw new InputValidationException("Extraction destination must have a parent directory.");
        parent.Create();
        var drive = new DriveInfo(Path.GetPathRoot(parent.FullName)!);
        var required = checked(report.ExpandedBytes + limits.MinimumFreeBytes);
        if (drive.AvailableFreeSpace < required)
            throw new InputValidationException($"Insufficient free space. Need {required:N0}, have {drive.AvailableFreeSpace:N0} bytes.");

        var temporary = Path.Combine(parent.FullName, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.partial");
        long totalWritten = 0;
        try
        {
            Directory.CreateDirectory(temporary);
            using var archive = ZipFile.OpenRead(snapshotPath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = ValidateEntryPath(entry.FullName, limits);
                ValidateEntryType(entry);
                var target = ResolveContainedPath(temporary, normalized);
                if (IsDirectory(entry))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var source = entry.Open();
                await using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var entryWritten = await CopyBoundedAsync(
                    source,
                    output,
                    entry.Length,
                    limits.MaxExpandedBytes - totalWritten,
                    cancellationToken);
                totalWritten = checked(totalWritten + entryWritten);
                if (entryWritten != entry.Length)
                    throw new InputSecurityException($"Entry size changed during extraction: {entry.FullName}");
            }

            if (totalWritten != report.ExpandedBytes)
                throw new InputSecurityException("Extracted byte total does not match the preflight report.");

            Directory.Move(temporary, destination);
        }
        catch
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
            throw;
        }
    }

    /// <summary>Normalizes and validates one archive entry path for safe Windows extraction.</summary>
    /// <param name="rawName">Entry name from the ZIP central directory.</param>
    /// <param name="limits">Path-length and depth limits.</param>
    /// <returns>A normalization-form-C relative path using forward slashes.</returns>
    /// <exception cref="InputSecurityException">The path is empty, absolute, traversing, ambiguous, reserved, or over a limit.</exception>
    internal static string ValidateEntryPath(string rawName, IngestLimits limits)
    {
        if (string.IsNullOrWhiteSpace(rawName) || rawName.Contains('\0'))
            throw new InputSecurityException("Archive contains an empty or NUL-bearing path.");

        var name = rawName.Replace('\\', '/').Normalize(NormalizationForm.FormC);
        if (name.StartsWith('/') || name.StartsWith("//", StringComparison.Ordinal) || DrivePathPattern().IsMatch(name))
            throw new InputSecurityException($"Archive contains an absolute path: {rawName}");

        var parts = name.Split('/', StringSplitOptions.None);
        if (parts[^1].Length == 0)
            parts = parts[..^1];
        if (parts.Length == 0 || parts.Length > limits.MaxPathDepth)
            throw new InputSecurityException($"Archive path depth is unsafe: {rawName}");
        if (name.Length > limits.MaxPathLength)
            throw new InputSecurityException($"Archive path exceeds {limits.MaxPathLength} characters: {rawName}");

        foreach (var part in parts)
        {
            if (part.Length == 0 || part is "." or ".." || part.Contains(':') || part.EndsWith(' ') || part.EndsWith('.'))
                throw new InputSecurityException($"Archive path is ambiguous or unsafe: {rawName}");
            var deviceName = part.Split('.', 2)[0];
            if (WindowsReservedNames.Contains(deviceName))
                throw new InputSecurityException($"Archive path uses a reserved device name: {rawName}");
        }

        return string.Join('/', parts);
    }

    private static void ValidateEntryType(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        var type = unixMode & UnixFileTypeMask;
        if (type == UnixSymbolicLink)
            throw new InputSecurityException($"Symbolic links are not accepted: {entry.FullName}");
        if (type is not (0 or UnixRegularFile or UnixDirectory))
            throw new InputSecurityException($"Special filesystem entries are not accepted: {entry.FullName}");
    }

    private static bool IsDirectory(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith('/') || string.IsNullOrEmpty(entry.Name);

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(rootWithSeparator, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InputSecurityException($"Archive path escapes extraction root: {relativePath}");
        return target;
    }

    private static async Task<string> ComputeSha256Async(string path, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maxBytes)
            throw new InputSecurityException($"Archive exceeds {maxBytes:N0} bytes.");
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long expectedBytes,
        long remainingBudget,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 1024];
        long written = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return written;
            written = checked(written + read);
            if (written > expectedBytes || written > remainingBudget)
                throw new InputSecurityException("ZIP entry expanded beyond its declared or job limit.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    [GeneratedRegex("^[A-Za-z]:", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex DrivePathPattern();
}
