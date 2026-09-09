using System.Text.Json;
using System.Security.Cryptography;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Models;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Pipeline;

/// <summary>Creates and opens content-addressed private ingestion jobs.</summary>
/// <param name="workRoot">Private root under which jobs are named by archive SHA-256.</param>
/// <param name="limits">Resource limits used to inspect and snapshot archives.</param>
/// <remarks>Job creation uses a partial directory and an atomic rename so incomplete snapshots are never exposed as resumable jobs.</remarks>
public sealed class JobStore(string workRoot, IngestLimits limits)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>Inspects an archive, copies an immutable snapshot, re-inspects it, and creates a new job.</summary>
    /// <param name="inputArchive">Path to the source ZIP archive.</param>
    /// <param name="cancellationToken">Token that cancels inspection, copying, or persistence.</param>
    /// <returns>The completed job paths and report for the immutable snapshot.</returns>
    /// <exception cref="InputValidationException">A job for the same archive already exists or the archive is invalid.</exception>
    /// <exception cref="InputSecurityException">A configured limit is exceeded or the source changes while being snapshotted.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>On failure, the newly created partial directory is deleted.</remarks>
    public async Task<(JobPaths Paths, ArchiveReport Report)> CreateAsync(
        string inputArchive,
        CancellationToken cancellationToken = default)
    {
        var sourceReport = await SecureZipArchive.InspectAsync(inputArchive, limits, cancellationToken: cancellationToken);
        var jobRoot = Path.Combine(Path.GetFullPath(workRoot), sourceReport.Sha256);
        if (Directory.Exists(jobRoot))
            throw new InputValidationException($"Job already exists. Use resume or remove it deliberately: {jobRoot}");

        var partialRoot = jobRoot + $".{Guid.NewGuid():N}.partial";
        try
        {
            Directory.CreateDirectory(partialRoot);
            var snapshot = Path.Combine(partialRoot, "input.zip");
            await CopySnapshotAsync(sourceReport.ArchivePath, snapshot, limits.MaxArchiveBytes, cancellationToken);
            var snapshotReport = await SecureZipArchive.InspectAsync(
                snapshot,
                limits,
                sourceReport.Sha256,
                cancellationToken);
            var finalReport = snapshotReport with { ArchivePath = Path.Combine(jobRoot, "input.zip") };

            var partialPaths = BuildPaths(partialRoot);
            await WriteJsonAsync(partialPaths.ArchiveReport, finalReport, cancellationToken);
            await WriteJsonAsync(partialPaths.State, new JobState("snapshotted", DateTimeOffset.UtcNow, null), cancellationToken);
            Directory.Move(partialRoot, jobRoot);
            return (BuildPaths(jobRoot), finalReport);
        }
        catch
        {
            if (Directory.Exists(partialRoot))
                Directory.Delete(partialRoot, recursive: true);
            throw;
        }
    }

    /// <summary>Revalidates an existing immutable snapshot and prepares its artifacts for another prepare attempt.</summary>
    /// <param name="inputArchive">Source archive used to locate and authenticate the content-addressed job.</param>
    /// <param name="cancellationToken">Token that cancels archive inspection or persistence reads.</param>
    /// <returns>The existing job paths and a freshly verified snapshot report.</returns>
    /// <exception cref="InputValidationException">The job is absent or is not at a resumable prepare state.</exception>
    /// <exception cref="InputSecurityException">The saved report no longer matches the snapshot.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>Existing extracted data and render plans are preserved under an attempt directory before retrying.</remarks>
    public async Task<(JobPaths Paths, ArchiveReport Report)> ResumePrepareAsync(
        string inputArchive,
        CancellationToken cancellationToken = default)
    {
        var sourceReport = await SecureZipArchive.InspectAsync(inputArchive, limits, cancellationToken: cancellationToken);
        var paths = Open(sourceReport.Sha256);
        var state = await ReadJsonAsync<JobState>(paths.State, cancellationToken);
        if (state.Stage != "snapshotted" && !(state.Stage == "failed" && state.FailedStage == "prepare"))
            throw new InputValidationException($"Job cannot resume prepare from stage '{state.Stage}'.");

        var savedReport = await ReadJsonAsync<ArchiveReport>(paths.ArchiveReport, cancellationToken);
        var currentReport = await SecureZipArchive.InspectAsync(
            paths.Snapshot, limits, sourceReport.Sha256, cancellationToken);
        if (savedReport.Sha256 != currentReport.Sha256 ||
            savedReport.ArchiveBytes != currentReport.ArchiveBytes ||
            savedReport.ExpandedBytes != currentReport.ExpandedBytes ||
            savedReport.EntryCount != currentReport.EntryCount ||
            savedReport.FileCount != currentReport.FileCount ||
            savedReport.DirectoryCount != currentReport.DirectoryCount ||
            !savedReport.LevelDatCandidates.SequenceEqual(currentReport.LevelDatCandidates, StringComparer.Ordinal))
        {
            throw new InputSecurityException("Immutable snapshot metadata no longer matches its archive report.");
        }

        PreservePrepareArtifacts(paths);
        return (paths, currentReport with { ArchivePath = paths.Snapshot });
    }

    /// <summary>Opens an existing job by its archive SHA-256 identifier.</summary>
    /// <param name="jobId">A 64-character hexadecimal SHA-256 digest.</param>
    /// <returns>The canonical paths for the existing job.</returns>
    /// <exception cref="InputValidationException"><paramref name="jobId"/> is malformed or does not identify an existing job.</exception>
    public JobPaths Open(string jobId)
    {
        if (jobId.Length != 64 || jobId.Any(character => !Uri.IsHexDigit(character)))
            throw new InputValidationException("Job ID must be a SHA-256 hex digest.");
        var paths = BuildPaths(Path.Combine(Path.GetFullPath(workRoot), jobId.ToLowerInvariant()));
        if (!Directory.Exists(paths.Root))
            throw new InputValidationException($"Job not found: {jobId}");
        return paths;
    }

    /// <summary>Builds canonical artifact paths for a job root without accessing the file system.</summary>
    /// <param name="root">Job root path.</param>
    /// <returns>The canonical job artifact paths.</returns>
    public static JobPaths BuildPaths(string root) => new(
        root,
        Path.Combine(root, "input.zip"),
        Path.Combine(root, "worlds"),
        Path.Combine(root, "render"),
        Path.Combine(root, "job-state.json"),
        Path.Combine(root, "archive-report.json"),
        Path.Combine(root, "render-plan.json"));

    /// <summary>Serializes JSON through an atomic temporary-file replacement.</summary>
    /// <typeparam name="T">Type of value to serialize.</typeparam>
    /// <param name="path">Destination JSON path.</param>
    /// <param name="value">Value to serialize.</param>
    /// <param name="cancellationToken">Token that cancels serialization.</param>
    /// <returns>A task that completes after the replacement is visible.</returns>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        AtomicFile.WriteJsonAsync(path, value, JsonOptions, cancellationToken);

    /// <summary>Reads a persisted job JSON artifact.</summary>
    /// <typeparam name="T">Expected artifact type.</typeparam>
    /// <param name="path">Path to the JSON file.</param>
    /// <param name="cancellationToken">Token that cancels deserialization.</param>
    /// <returns>The deserialized value.</returns>
    /// <exception cref="InputValidationException">The document deserializes to <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document is malformed or incompatible with <typeparamref name="T"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<T>(input, JsonOptions, cancellationToken)
            ?? throw new InputValidationException($"JSON file is empty or invalid: {path}");
    }

            /// <summary>Computes the lowercase SHA-256 digest of a file.</summary>
            /// <param name="path">Path to the file.</param>
            /// <param name="cancellationToken">Token that cancels hashing.</param>
            /// <returns>The lowercase hexadecimal digest.</returns>
            /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, cancellationToken));
    }

    private static async Task CopySnapshotAsync(
        string sourcePath,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > maxBytes)
                throw new InputSecurityException("Archive grew beyond the configured limit while snapshotting.");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await destination.FlushAsync(cancellationToken);
    }

    private static void PreservePrepareArtifacts(JobPaths paths)
    {
        if (!Directory.Exists(paths.Extracted) && !File.Exists(paths.RenderPlan))
            return;

        var retryRoot = Path.Combine(
            paths.Root,
            "attempts",
            $"prepare-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(retryRoot);
        if (Directory.Exists(paths.Extracted))
            Directory.Move(paths.Extracted, Path.Combine(retryRoot, "worlds"));
        if (File.Exists(paths.RenderPlan))
            File.Move(paths.RenderPlan, Path.Combine(retryRoot, "render-plan.json"));
    }
}

/// <summary>Records the durable stage and last transition details for an ingestion job.</summary>
/// <param name="Stage">Current stage name.</param>
/// <param name="UpdatedAt">UTC transition timestamp.</param>
/// <param name="Detail">Optional human-readable transition detail.</param>
/// <param name="FailedStage">Stage that failed, when <paramref name="Stage"/> is <c>failed</c>.</param>
public sealed record JobState(
    string Stage,
    DateTimeOffset UpdatedAt,
    string? Detail,
    string? FailedStage = null);

/// <summary>Enforces legal entry states for mutating pipeline stages.</summary>
/// <remarks>Each method fails closed when state is missing, unknown, or belongs to another stage.</remarks>
public static class JobStatePolicy
{
    /// <summary>Requires a prepared job, or an explicitly resumed render failure.</summary>
    /// <param name="state">Current durable job state.</param>
    /// <param name="resume">Whether a failed render is being resumed.</param>
    /// <exception cref="InputValidationException">Rendering is not legal from the current state.</exception>
    public static void RequireRender(JobState state, bool resume)
    {
        if (state.Stage == "prepared")
            return;
        if (resume && state.Stage == "failed" && state.FailedStage == "render")
            return;
        throw new InputValidationException(
            $"Render cannot start from stage '{state.Stage}'{FormatFailedStage(state)}.");
    }

            /// <summary>Requires adapted or already published/registerable output.</summary>
            /// <param name="state">Current durable job state.</param>
            /// <exception cref="InputValidationException">Publication is not legal from the current state.</exception>
    public static void RequirePublish(JobState state)
    {
        if (state.Stage is "adapted" or "published" or "registered-unpublished")
            return;
        throw new InputValidationException(
            $"Publish cannot start from stage '{state.Stage}'{FormatFailedStage(state)}.");
    }

            /// <summary>Requires rendered output, an existing downstream state, or a failed adaptation retry.</summary>
            /// <param name="state">Current durable job state.</param>
            /// <exception cref="InputValidationException">Adaptation is not legal from the current state.</exception>
    public static void RequireAdapt(JobState state)
    {
        if (state.Stage is "rendered" or "adapted" or "published" or "registered-unpublished")
            return;
        if (state.Stage == "failed" && state.FailedStage == "adapt")
            return;
        throw new InputValidationException(
            $"Tile adaptation cannot start from stage '{state.Stage}'{FormatFailedStage(state)}.");
    }

    private static string FormatFailedStage(JobState state) =>
        state.FailedStage is null ? string.Empty : $" after {state.FailedStage}";
}

/// <summary>Persists JSON without exposing a partially written destination file.</summary>
internal static class AtomicFile
{
    /// <summary>Writes JSON to a unique sibling file and atomically replaces the destination.</summary>
    /// <typeparam name="T">Type of value to serialize.</typeparam>
    /// <param name="path">Destination path.</param>
    /// <param name="value">Value to serialize.</param>
    /// <param name="options">Serializer options.</param>
    /// <param name="cancellationToken">Token that cancels serialization.</param>
    /// <returns>A task that completes after replacement and temporary-file cleanup.</returns>
    /// <remarks>The temporary file is deleted on cancellation or failure.</remarks>
    public static async Task WriteJsonAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, value, options, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
