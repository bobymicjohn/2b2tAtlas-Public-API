using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Atlas;
using Atlas.Ingestor.Configuration;
using Atlas.Ingestor.Models;
using Atlas.Ingestor.Pipeline;
using Atlas.Ingestor.Security;

namespace Atlas.Ingestor.Rendering;

/// <summary>Defines a validated, versioned, hash-pinned renderer profile.</summary>
/// <param name="ExecutablePath">Absolute path to the approved uNmINeD CLI executable.</param>
/// <param name="ExpectedSha256">Expected lowercase SHA-256 digest of the executable.</param>
/// <param name="Version">Operator-supplied renderer/profile version label.</param>
/// <param name="DimensionArguments">Argument arrays keyed by canonical dimension.</param>
/// <param name="Timeout">Maximum duration of each renderer process.</param>
/// <param name="MaxLogBytes">Maximum bytes in each standard-output or standard-error log.</param>
/// <param name="NightColorGrade">Optional colour-preserving grade applied to native night tiles.</param>
/// <param name="Resume">Whether existing output may be resumed after provenance validation.</param>
public sealed record RendererOptions(
    string ExecutablePath,
    string ExpectedSha256,
    string Version,
    IReadOnlyDictionary<string, string[]> DimensionArguments,
    TimeSpan Timeout,
    long MaxLogBytes,
    NightColorGradeOptions? NightColorGrade = null,
    bool Resume = false)
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "executablePath", "expectedSha256", "version", "dimensionArguments", "nightColorGrade",
    };

    /// <summary>Loads a strict renderer profile and applies runtime timeout and log ceilings.</summary>
    /// <param name="profilePath">Path to the renderer profile JSON.</param>
    /// <param name="timeout">Maximum duration assigned to each renderer process.</param>
    /// <param name="maxLogBytes">Maximum bytes assigned to each renderer log.</param>
    /// <param name="cancellationToken">Token that cancels profile parsing.</param>
    /// <returns>A validated profile with an absolute executable path.</returns>
    /// <exception cref="InputValidationException">The schema, hash, version, dimension keys, arguments, or placeholders are invalid.</exception>
    /// <exception cref="JsonException">The profile is malformed JSON or exceeds the depth limit.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>Unknown keys and placeholders are rejected so misspelled controls cannot be silently ignored.</remarks>
    public static async Task<RendererOptions> LoadAsync(
        string profilePath,
        TimeSpan timeout,
        long maxLogBytes,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(profilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var document = await JsonDocument.ParseAsync(input, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 16,
        }, cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InputValidationException("Renderer profile must be a JSON object.");
        foreach (var property in root.EnumerateObject())
        {
            if (!AllowedKeys.Contains(property.Name))
                throw new InputValidationException($"Unknown renderer profile key: {property.Name}");
        }

        var profileDirectory = Path.GetDirectoryName(Path.GetFullPath(profilePath))!;
        var executableValue = ReadRequiredString(root, "executablePath");
        var executablePath = Path.GetFullPath(executableValue, profileDirectory);
        var hash = ReadRequiredString(root, "expectedSha256").ToLowerInvariant();
        if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new InputValidationException("Renderer expectedSha256 must be a 64-character hex digest.");
        var version = ReadRequiredString(root, "version");
        if (version.Length > 100 || version.Any(char.IsControl))
            throw new InputValidationException("Renderer version is invalid.");

        if (!root.TryGetProperty("dimensionArguments", out var commands) || commands.ValueKind != JsonValueKind.Object)
            throw new InputValidationException("Renderer profile dimensionArguments must be an object.");
        var parsedCommands = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var command in commands.EnumerateObject())
        {
            if (command.Name is not ("overworld" or "nether" or "end") || command.Value.ValueKind != JsonValueKind.Array)
                throw new InputValidationException($"Invalid renderer dimension profile: {command.Name}");
            var arguments = command.Value.EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.String
                    ? value.GetString()!
                    : throw new InputValidationException("Renderer arguments must be strings."))
                .ToArray();
            ValidateArguments(arguments);
            parsedCommands.Add(command.Name, arguments);
        }
        if (parsedCommands.Count == 0)
            throw new InputValidationException("Renderer profile must configure at least one dimension.");

        var nightColorGrade = ReadNightColorGrade(root);
        return new RendererOptions(executablePath, hash, version, parsedCommands, timeout, maxLogBytes, nightColorGrade);
    }

    private static NightColorGradeOptions ReadNightColorGrade(JsonElement root)
    {
        if (!root.TryGetProperty("nightColorGrade", out var value)) return new NightColorGradeOptions();
        if (value.ValueKind != JsonValueKind.Object)
            throw new InputValidationException("Renderer nightColorGrade must be an object.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "enabled", "saturation", "lightness" };
        foreach (var property in value.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new InputValidationException($"Unknown nightColorGrade key: {property.Name}");
        var result = new NightColorGradeOptions
        {
            Enabled = value.TryGetProperty("enabled", out var enabled) ? enabled.GetBoolean() : true,
            Saturation = value.TryGetProperty("saturation", out var saturation) ? saturation.GetDouble() : 1.2,
            Lightness = value.TryGetProperty("lightness", out var lightness) ? lightness.GetDouble() : 1.15,
        };
        if (!double.IsFinite(result.Saturation) || result.Saturation is < 0 or > 2)
            throw new InputValidationException("Renderer night saturation must be between 0 and 2.");
        if (!double.IsFinite(result.Lightness) || result.Lightness is < 0.5 or > 2)
            throw new InputValidationException("Renderer night lightness must be between 0.5 and 2.");
        return result;
    }

    private static string ReadRequiredString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InputValidationException($"Renderer profile {key} is required.");

    private static void ValidateArguments(string[] arguments)
    {
        if (arguments.Length is < 2 or > 100 || arguments.Any(value => value.Length is 0 or > 1000 || value.Any(char.IsControl)))
            throw new InputValidationException("Renderer argument list is empty or exceeds safety limits.");
        if (arguments.Any(value => value
                .Replace("{world}", string.Empty, StringComparison.Ordinal)
                .Replace("{output}", string.Empty, StringComparison.Ordinal)
                .IndexOfAny(['{', '}']) >= 0))
            throw new InputValidationException("Renderer arguments contain an unknown placeholder.");
        if (arguments.Count(value => value.Contains("{world}", StringComparison.Ordinal)) != 1 ||
            arguments.Count(value => value.Contains("{output}", StringComparison.Ordinal)) != 1)
        {
            throw new InputValidationException("Renderer arguments require exactly one {world} and {output} placeholder.");
        }
    }
}

/// <summary>Binds renderer output to the exact plan, world, binary, profile arguments, and completion state.</summary>
/// <param name="PlanSha256">SHA-256 of the render plan.</param>
/// <param name="Dimension">Canonical rendered dimension.</param>
/// <param name="WorldRoot">Absolute world root passed to the renderer.</param>
/// <param name="RendererVersion">Configured renderer/profile version label.</param>
/// <param name="RendererSha256">Pinned renderer executable digest.</param>
/// <param name="Arguments">Unexpanded profile argument template used for this dimension.</param>
/// <param name="Status">Render status, normally <c>started</c> or <c>completed</c>.</param>
/// <param name="UpdatedAtUtc">UTC timestamp of the latest status write.</param>
/// <param name="NightColorGrade">Colour grade applied after native night rendering, when present.</param>
public sealed record RenderProvenance(
    string PlanSha256,
    string Dimension,
    string WorldRoot,
    string RendererVersion,
    string RendererSha256,
    IReadOnlyList<string> Arguments,
    string Status,
    DateTimeOffset UpdatedAtUtc,
    NightColorGradeOptions? NightColorGrade = null);

/// <summary>Runs a pinned uNmINeD process for each planned dimension under bounded execution controls.</summary>
public static class RendererRunner
{
    /// <summary>Authenticates and executes the configured renderer, then marks each dimension provenance complete.</summary>
    /// <param name="options">Validated renderer profile and runtime ceilings.</param>
    /// <param name="plan">Render plan whose dimensions will be processed sequentially.</param>
    /// <param name="planSha256">Digest of the persisted plan.</param>
    /// <param name="outputRoot">Private staging root for output, provenance, and logs.</param>
    /// <param name="cancellationToken">Token that cancels hashing, process execution, log copying, and persistence.</param>
    /// <returns>A task that completes after every selected dimension exits successfully.</returns>
    /// <exception cref="InputValidationException">The executable is absent/unapproved, a dimension is unsupported, or output cannot safely start or resume.</exception>
    /// <exception cref="InputSecurityException">The executable hash or resume provenance does not match.</exception>
    /// <exception cref="IngestException">The process fails to start, times out, exceeds log limits, or exits nonzero.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation was requested.</exception>
    /// <remarks>
    /// Arguments are supplied through <see cref="ProcessStartInfo.ArgumentList"/> and the child environment is cleared
    /// except for renderer-local <c>PATH</c> and temporary-directory variables. On timeout, cancellation, or failure,
    /// the active process tree is killed and awaited. Completed output from earlier dimensions may remain for diagnosis.
    /// </remarks>
    public static async Task RenderAsync(
        RendererOptions options,
        RenderPlan plan,
        string planSha256,
        string outputRoot,
        CancellationToken cancellationToken = default)
    {
        var executable = Path.GetFullPath(options.ExecutablePath);
        if (!File.Exists(executable))
            throw new InputValidationException($"Renderer executable was not found: {executable}");
        if (!Path.GetFileName(executable).StartsWith("unmined-cli", StringComparison.OrdinalIgnoreCase))
            throw new InputValidationException("Renderer executable must be an approved uNmINeD CLI binary.");

        await using (var renderer = File.OpenRead(executable))
        {
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(renderer, cancellationToken));
            if (!actual.Equals(options.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InputSecurityException("Renderer executable SHA-256 does not match the configured pin.");
        }

        foreach (var dimension in plan.Dimensions)
        {
            if (!options.DimensionArguments.ContainsKey(dimension.Key))
                throw new InputValidationException($"Renderer profile {options.Version} does not support dimension: {dimension.Key}");
        }

        Directory.CreateDirectory(outputRoot);
        foreach (var dimension in plan.Dimensions)
        {
            var dimensionOutput = Path.Combine(outputRoot, dimension.Key);
            var provenancePath = Path.Combine(outputRoot, $".atlas-render-provenance-{dimension.Key}.json");
            var configuredArguments = options.DimensionArguments[dimension.Key];
            var expectedProvenance = new RenderProvenance(
                planSha256,
                dimension.Key,
                plan.World.RootPath,
                options.Version,
                options.ExpectedSha256,
                configuredArguments,
                "started",
                DateTimeOffset.UtcNow);
            await RenderProvenanceStore.ValidateAndWriteAsync(
                provenancePath, dimensionOutput, expectedProvenance, options.Resume, cancellationToken);

            var arguments = configuredArguments.Select(argument => argument
                .Replace("{world}", plan.World.RootPath, StringComparison.Ordinal)
                .Replace("{output}", dimensionOutput, StringComparison.Ordinal))
                .ToArray();

            await RunProcessAsync(
                executable,
                arguments,
                Path.Combine(outputRoot, $"unmined-{dimension.Key}-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}"),
                options.Timeout,
                options.MaxLogBytes,
                cancellationToken);

            await JobStore.WriteJsonAsync(
                provenancePath,
                expectedProvenance with { Status = "completed", UpdatedAtUtc = DateTimeOffset.UtcNow },
                cancellationToken);
        }
    }

    private static async Task RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string logBasePath,
        TimeSpan timeout,
        long maxLogBytes,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.Environment.Clear();
        process.StartInfo.Environment["PATH"] = Path.GetDirectoryName(executable)!;
        process.StartInfo.Environment["TEMP"] = Path.GetTempPath();
        process.StartInfo.Environment["TMP"] = Path.GetTempPath();

        var stdoutPath = logBasePath + ".log";
        var stderrPath = logBasePath + ".error.log";
        await using var stdoutLog = new FileStream(stdoutPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        await using var stderrLog = new FileStream(stderrPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (stdoutLog.Length > maxLogBytes || stderrLog.Length > maxLogBytes)
            throw new IngestException($"Existing renderer log exceeds {maxLogBytes:N0} bytes.");
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        var processStarted = false;
        try
        {
            if (!process.Start())
                throw new IngestException("Renderer failed to start.");
            processStarted = true;
            var stdout = CopyLogAsync(process.StandardOutput, stdoutLog, maxLogBytes, linkedSource.Token);
            var stderr = CopyLogAsync(process.StandardError, stderrLog, maxLogBytes, linkedSource.Token);
            await Task.WhenAll(process.WaitForExitAsync(linkedSource.Token), stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            if (processStarted && !process.HasExited)
                process.Kill(entireProcessTree: true);
            if (processStarted)
                await process.WaitForExitAsync(CancellationToken.None);
            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                throw new IngestException($"Renderer exceeded its {timeout} timeout.");
            throw;
        }
        catch
        {
            if (processStarted && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            throw;
        }

        if (process.ExitCode != 0)
            throw new IngestException($"Renderer exited with code {process.ExitCode}. See {stdoutPath} and {stderrPath}");
    }

    private static async Task CopyLogAsync(
        StreamReader source,
        FileStream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return;
            var bytes = System.Text.Encoding.UTF8.GetBytes(buffer, 0, read);
            await destination.WriteAsync(bytes, cancellationToken);
            if (destination.Length > maxBytes)
                throw new IngestException($"Renderer log exceeded {maxBytes:N0} bytes.");
        }
    }
}

/// <summary>Controls initial and resumed render output through durable provenance.</summary>
public static class RenderProvenanceStore
{
    /// <summary>Validates output/provenance presence and writes a fresh <c>started</c> record.</summary>
    /// <param name="provenancePath">Path to the dimension provenance JSON.</param>
    /// <param name="dimensionOutput">Dimension output directory.</param>
    /// <param name="expected">Expected plan, world, renderer, and argument identity.</param>
    /// <param name="resume">Whether matching existing output may be resumed.</param>
    /// <param name="cancellationToken">Token that cancels provenance reads or writes.</param>
    /// <returns>A task that completes after the fresh provenance record is atomically persisted.</returns>
    /// <exception cref="InputValidationException">Fresh output already exists, or resume lacks either output or provenance.</exception>
    /// <exception cref="InputSecurityException">Existing provenance belongs to another plan, world, renderer, or argument set.</exception>
    /// <exception cref="OperationCanceledException">The operation was canceled.</exception>
    /// <remarks>Resume fails closed unless both output and provenance exist and all identity fields match.</remarks>
    public static async Task ValidateAndWriteAsync(
        string provenancePath,
        string dimensionOutput,
        RenderProvenance expected,
        bool resume,
        CancellationToken cancellationToken)
    {
        var outputExists = Directory.Exists(dimensionOutput);
        var provenanceExists = File.Exists(provenancePath);
        if (!resume)
        {
            if (outputExists || provenanceExists)
                throw new InputValidationException($"Renderer output or provenance already exists: {dimensionOutput}");
            await JobStore.WriteJsonAsync(provenancePath, expected, cancellationToken);
            return;
        }

        if (!outputExists || !provenanceExists)
            throw new InputValidationException($"Resume requires both existing output and provenance: {dimensionOutput}");
        var actual = await JobStore.ReadJsonAsync<RenderProvenance>(provenancePath, cancellationToken);
        if (!Matches(actual, expected))
            throw new InputSecurityException($"Renderer provenance does not match the current plan/profile: {dimensionOutput}");
        await JobStore.WriteJsonAsync(provenancePath, expected, cancellationToken);
    }

    /// <summary>Compares the identity-bearing fields used to authorize render resume.</summary>
    /// <param name="actual">Persisted provenance.</param>
    /// <param name="expected">Current expected provenance.</param>
    /// <returns><see langword="true"/> when plan, dimension, world, renderer, and arguments match.</returns>
    /// <remarks>Status and timestamps are intentionally excluded from resume identity.</remarks>
    public static bool Matches(RenderProvenance actual, RenderProvenance expected) =>
        actual.PlanSha256.Equals(expected.PlanSha256, StringComparison.OrdinalIgnoreCase) &&
        actual.Dimension == expected.Dimension &&
        Path.GetFullPath(actual.WorldRoot).Equals(Path.GetFullPath(expected.WorldRoot), StringComparison.OrdinalIgnoreCase) &&
        actual.RendererVersion == expected.RendererVersion &&
        actual.RendererSha256.Equals(expected.RendererSha256, StringComparison.OrdinalIgnoreCase) &&
        actual.Arguments.SequenceEqual(expected.Arguments, StringComparer.Ordinal) &&
        GradeMatches(actual.NightColorGrade, expected.NightColorGrade);

    private static bool GradeMatches(NightColorGradeOptions? actual, NightColorGradeOptions? expected) =>
        (actual?.Enabled ?? false) == (expected?.Enabled ?? false) &&
        Math.Abs((actual?.Saturation ?? 0) - (expected?.Saturation ?? 0)) < 0.000001 &&
        Math.Abs((actual?.Lightness ?? 0) - (expected?.Lightness ?? 0)) < 0.000001;
}
